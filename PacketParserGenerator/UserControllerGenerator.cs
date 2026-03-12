#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace PacketParserGenerator
{
    /// <summary>
    /// [UserController] Source Generator
    /// Reflection 대신 컴파일 타임에 핸들러 등록 코드 생성
    ///
    /// 생성 코드:
    /// - AddGeneratedUserControllers(IServiceCollection) — Scoped DI + IUserHandlerRegistrar 등록
    /// - RegisterHandlers(MessageDispatcher) — 핸들러 delegate 등록 (no reflection)
    /// </summary>
    [Generator]
    public class UserControllerGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var controllerClasses = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: (s, _) => IsCandidateClass(s),
                    transform: (ctx, _) => GetControllerModel(ctx))
                .Where(m => m != null);

            var assemblyName = context.CompilationProvider
                .Select((c, _) => c.AssemblyName ?? "Generated");

            var combined = controllerClasses.Collect().Combine(assemblyName);

            context.RegisterSourceOutput(combined, (ctx, pair) => Execute(ctx, pair.Left, pair.Right));
        }

        private bool IsCandidateClass(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax c && c.AttributeLists.Count > 0;
        }

        private ControllerModel? GetControllerModel(GeneratorSyntaxContext context)
        {
            var classDeclaration = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;

            if (symbol == null) return null;

            var attributes = symbol.GetAttributes();
            if (!attributes.Any(a => a.AttributeClass?.Name == "UserControllerAttribute"))
                return null;

            var methods = new List<HandlerMethod>();

            foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
            {
                var attr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "UserPacketHandlerAttribute");
                if (attr != null && attr.ConstructorArguments.Length > 0)
                {
                    var msgId = Convert.ToInt32(attr.ConstructorArguments[0].Value!);

                    // Handler 시그니처: Task<Response> Method(ISessionActor actor, TMessage req)
                    if (member.Parameters.Length != 2 ||
                        member.Parameters[0].Type.Name != "ISessionActor" ||
                        member.ReturnType.Name != "Task")
                    {
                        continue;
                    }

                    var requestType = member.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                    // RequireActorState 어트리뷰트 확인
                    var stateAttr = member.GetAttributes()
                        .FirstOrDefault(a => a.AttributeClass?.Name == "RequireActorStateAttribute");
                    var allowedStates = new List<string>();
                    if (stateAttr != null)
                    {
                        foreach (var arg in stateAttr.ConstructorArguments)
                        {
                            if (arg.Kind == TypedConstantKind.Array)
                            {
                                foreach (var item in arg.Values)
                                {
                                    allowedStates.Add(((int)item.Value!).ToString());
                                }
                            }
                            else if (arg.Value != null)
                            {
                                allowedStates.Add(((int)arg.Value).ToString());
                            }
                        }
                    }

                    // 외부 어셈블리 여부 확인 (Proto 어셈블리 ModuleInitializer 강제 실행용)
                    var requestTypeSymbol = member.Parameters[1].Type;
                    var isExternal = !SymbolEqualityComparer.Default.Equals(
                        requestTypeSymbol.ContainingAssembly,
                        context.SemanticModel.Compilation.Assembly);

                    methods.Add(new HandlerMethod(member.Name, msgId, requestType, allowedStates, isExternal));
                }
            }

            if (methods.Count == 0)
                return null;

            var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return new ControllerModel(fqn, symbol.Name, methods);
        }

        private void Execute(SourceProductionContext context, ImmutableArray<ControllerModel?> controllers, string assemblyName)
        {
            var validControllers = controllers.Where(c => c != null).ToList();
            if (validControllers.Count == 0) return;

            // Assembly name 기반 namespace (프로젝트별 고유)
            var ns = $"{assemblyName}.Generated";

            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Source Generator가 생성한 UserController 등록 코드");
            sb.AppendLine("    /// Reflection 없이 컴파일 타임에 직접 호출 코드 생성");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class GeneratedUserControllerRegistration");
            sb.AppendLine("    {");

            // AddGeneratedUserControllers extension method
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Source Generator가 생성한 UserController를 DI에 등록합니다.");
            sb.AppendLine("        /// Controller Scoped DI 등록 + IUserHandlerRegistrar 자동 등록.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedUserControllers(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
            sb.AppendLine("        {");

            // 외부 Proto 어셈블리의 [ModuleInitializer] 강제 실행
            // (const MsgId 인라인으로 어셈블리가 자동 로드되지 않아 AutoGeneratedParsers 등록 누락 방지)
            var externalTypes = validControllers
                .SelectMany(c => c!.Methods)
                .Where(m => m.IsExternalRequestType)
                .Select(m => m.RequestType)
                .Distinct()
                .ToList();

            // 어셈블리(Module) 단위로 중복 제거: 같은 네임스페이스의 타입은 같은 Module
            var emittedModules = new HashSet<string>();
            foreach (var requestType in externalTypes)
            {
                // 네임스페이스 추출하여 Module 단위 중복 제거
                var lastDot = requestType.LastIndexOf('.');
                var moduleKey = lastDot >= 0 ? requestType.Substring(0, lastDot) : requestType;
                if (emittedModules.Add(moduleKey))
                {
                    sb.AppendLine($"            global::System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof({requestType}).Module.ModuleHandle);");
                }
            }

            foreach (var controller in validControllers)
            {
                sb.AppendLine($"            services.AddScoped<{controller!.FullyQualifiedName}>();");
            }
            sb.AppendLine("            services.AddSingleton<global::SimpleNetEngine.Game.Actor.IUserHandlerRegistrar>(new UserHandlerRegistrar());");
            sb.AppendLine("            return services;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // RegisterHandlers (used by registrar)
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// MessageDispatcher에 핸들러 등록 (Zero-Reflection, 직접 호출)");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static void RegisterHandlers(global::SimpleNetEngine.Game.Actor.MessageDispatcher dispatcher)");
            sb.AppendLine("        {");

            foreach (var controller in validControllers)
            {
                foreach (var method in controller!.Methods)
                {
                    sb.AppendLine($"            // {controller.ClassName}.{method.Name}");

                    // RequireActorState 배열 생성
                    var statesParam = method.AllowedStates.Count > 0
                        ? $", new global::SimpleNetEngine.Game.Actor.ActorState[] {{ {string.Join(", ", method.AllowedStates.Select(s => $"(global::SimpleNetEngine.Game.Actor.ActorState){s}"))} }}"
                        : "";

                    sb.AppendLine($"            dispatcher.RegisterHandler({method.MsgId}, async (sp, actor, payload) =>");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                var parser = global::SimpleNetEngine.ProtoGenerator.AutoGeneratedParsers.GetParserById({method.MsgId});");
                    sb.AppendLine("                if (parser == null) return null;");
                    sb.AppendLine("                var (header, message) = global::SimpleNetEngine.Game.Extensions.PacketHelper.ParseClientPacket(payload.Span, parser);");
                    sb.AppendLine($"                var controller = sp.GetRequiredService<{controller.FullyQualifiedName}>();");
                    sb.AppendLine($"                return await controller.{method.Name}(actor, ({method.RequestType})message);");
                    sb.AppendLine($"            }}{statesParam});");
                }
            }

            sb.AppendLine("        }");
            sb.AppendLine();

            // IUserHandlerRegistrar 구현
            sb.AppendLine("        private sealed class UserHandlerRegistrar : global::SimpleNetEngine.Game.Actor.IUserHandlerRegistrar");
            sb.AppendLine("        {");
            sb.AppendLine("            public void RegisterHandlers(global::SimpleNetEngine.Game.Actor.MessageDispatcher dispatcher)");
            sb.AppendLine("                => GeneratedUserControllerRegistration.RegisterHandlers(dispatcher);");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            context.AddSource("UserControllerRegistration.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private class ControllerModel
        {
            public string FullyQualifiedName { get; }
            public string ClassName { get; }
            public List<HandlerMethod> Methods { get; }

            public ControllerModel(string fullyQualifiedName, string className, List<HandlerMethod> methods)
            {
                FullyQualifiedName = fullyQualifiedName;
                ClassName = className;
                Methods = methods;
            }
        }

        private class HandlerMethod
        {
            public string Name { get; }
            public int MsgId { get; }
            public string RequestType { get; }
            public List<string> AllowedStates { get; }
            public bool IsExternalRequestType { get; }

            public HandlerMethod(string name, int msgId, string requestType, List<string> allowedStates, bool isExternalRequestType)
            {
                Name = name;
                MsgId = msgId;
                RequestType = requestType;
                AllowedStates = allowedStates;
                IsExternalRequestType = isExternalRequestType;
            }
        }
    }
}
