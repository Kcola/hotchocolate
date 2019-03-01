﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Resolvers;
using HotChocolate.Stitching.Delegation;
using HotChocolate.Stitching.Utilities;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;

namespace HotChocolate.Stitching
{
    public class DelegateToRemoteSchemaMiddleware
    {
        private static readonly RootScopedVariableResolver _resolvers =
            new RootScopedVariableResolver();
        private readonly FieldDelegate _next;

        public DelegateToRemoteSchemaMiddleware(FieldDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(IMiddlewareContext context)
        {
            DelegateDirective delegateDirective = context.Field
                .Directives[DirectiveNames.Delegate]
                .FirstOrDefault()?.ToObject<DelegateDirective>();

            if (delegateDirective != null)
            {
                IImmutableStack<SelectionPathComponent> path =
                    delegateDirective.Path is null
                    ? ImmutableStack<SelectionPathComponent>.Empty
                    : SelectionPathParser.Parse(delegateDirective.Path);

                IRemoteQueryRequest request =
                    CreateQuery(context, delegateDirective.Schema, path);

                IReadOnlyQueryResult result = await ExecuteQueryAsync(
                    context, request, delegateDirective.Schema)
                    .ConfigureAwait(false);

                context.ScopedContextData = context.ScopedContextData.SetItem(
                    WellKnownProperties.SchemaName, delegateDirective.Schema);
                context.Result = ExtractData(result.Data, path.Count());
                ReportErrors(context, result.Errors);
            }

            await _next.Invoke(context).ConfigureAwait(false);
        }

        private static IRemoteQueryRequest CreateQuery(
            IMiddlewareContext context,
            NameString schemaName,
            IImmutableStack<SelectionPathComponent> path)
        {
            var fieldRewriter = new ExtractFieldQuerySyntaxRewriter(
                context.Schema,
                context.Service<IEnumerable<IQueryDelegationRewriter>>());

            OperationType operationType =
                context.Schema.IsRootType(context.ObjectType)
                    ? context.Operation.Operation
                    : OperationType.Query;

            ExtractedField extractedField = fieldRewriter.ExtractField(
                schemaName, context.Document, context.Operation,
                context.FieldSelection, context.ObjectType);

            IEnumerable<VariableValue> scopedVariables =
                ResolveScopedVariables(
                    context, schemaName, operationType, path);

            IReadOnlyCollection<VariableValue> variableValues =
                CreateVariableValues(
                    context, scopedVariables, extractedField);

            DocumentNode query = RemoteQueryBuilder.New()
                .SetOperation(operationType)
                .SetSelectionPath(path)
                .SetRequestField(extractedField.Field)
                .AddVariables(CreateVariableDefs(variableValues))
                .AddFragmentDefinitions(extractedField.Fragments)
                .Build();

            var requestBuilder = new RemoteQueryRequestBuilder();

            AddVariables(context.Schema, schemaName,
                requestBuilder, query, variableValues);

            requestBuilder.SetQuery(query);
            requestBuilder.AddProperty(
                WellKnownProperties.IsAutoGenerated,
                true);

            return requestBuilder.Create();
        }

        private static async Task<IReadOnlyQueryResult> ExecuteQueryAsync(
            IResolverContext context,
            IReadOnlyQueryRequest request,
            string schemaName)
        {
            IRemoteQueryClient remoteQueryClient =
                context.Service<IStitchingContext>()
                    .GetRemoteQueryClient(schemaName);

            IExecutionResult result = await remoteQueryClient
                    .ExecuteAsync(request)
                    .ConfigureAwait(false);

            if (result is IReadOnlyQueryResult queryResult)
            {
                return queryResult;
            }

            // TODO : resources
            throw new QueryException(
                "Only query results are supported in the " +
                "delegation middleware.");
        }

        private static object ExtractData(
            IReadOnlyDictionary<string, object> data,
            int levels)
        {
            if (data.Count == 0)
            {
                return null;
            }

            object obj = data.Count == 0 ? null : data.First().Value;

            if (obj != null && levels > 1)
            {
                for (int i = levels - 1; i >= 1; i--)
                {
                    var current = obj as IReadOnlyDictionary<string, object>;
                    obj = current.Count == 0 ? null : current.First().Value;
                    if (obj is null)
                    {
                        return null;
                    }
                }
            }

            return obj;
        }

        private static void ReportErrors(
            IResolverContext context,
            IEnumerable<IError> errors)
        {
            IReadOnlyCollection<object> path = context.Path.ToCollection();

            foreach (IError error in errors)
            {
                context.ReportError(error.AddExtension("remote", path));
            }
        }

        private static IReadOnlyCollection<VariableValue> CreateVariableValues(
            IMiddlewareContext context,
            IEnumerable<VariableValue> scopedVaribles,
            ExtractedField extractedField)
        {
            var values = new Dictionary<string, VariableValue>();

            foreach (VariableValue value in scopedVaribles)
            {
                values[value.Name] = value;
            }

            IReadOnlyDictionary<string, object> requestVariables =
                context.GetVariables();

            foreach (VariableValue value in ResolveUsedRequestVariables(
                extractedField, requestVariables))
            {
                values[value.Name] = value;
            }

            return values.Values;
        }

        private static IReadOnlyList<VariableValue> ResolveScopedVariables(
            IResolverContext context,
            NameString schemaName,
            OperationType operationType,
            IEnumerable<SelectionPathComponent> components)
        {
            var stitchingContext = context.Service<IStitchingContext>();

            ISchema remoteSchema =
                stitchingContext.GetRemoteSchema(schemaName);

            IComplexOutputType type =
                remoteSchema.GetOperationType(operationType);

            var variables = new List<VariableValue>();
            SelectionPathComponent[] comps = components.Reverse().ToArray();

            for (int i = 0; i < comps.Length; i++)
            {
                SelectionPathComponent component = comps[i];

                if (!type.Fields.TryGetField(component.Name.Value,
                    out IOutputField field))
                {
                    // TODO : RESOURCES
                    throw new QueryException(new Error
                    {
                        Message = "RESOURCES"
                    });
                }

                ResolveScopedVariableArguments(
                    context, component, field, variables);

                if (i + 1 < comps.Length)
                {
                    if (!field.Type.IsComplexType())
                    {
                        // TODO : RESOURCES
                        throw new QueryException(new Error
                        {
                            Message = "RESOURCES"
                        });
                    }
                    type = (IComplexOutputType)field.Type.NamedType();
                }
            }

            return variables;
        }

        private static void ResolveScopedVariableArguments(
            IResolverContext context,
            SelectionPathComponent component,
            IOutputField field,
            ICollection<VariableValue> variables)
        {
            foreach (ArgumentNode argument in component.Arguments)
            {
                if (!field.Arguments.TryGetField(argument.Name.Value,
                    out IInputField arg))
                {
                    // TODO : RESOURCES
                    throw new QueryException(new Error
                    {
                        Message = "RESOURCES"
                    });
                }

                if (argument.Value is ScopedVariableNode sv)
                {
                    variables.Add(_resolvers.Resolve(
                        context, sv, arg.Type.ToTypeNode()));
                }
            }
        }

        private static IEnumerable<VariableValue> ResolveUsedRequestVariables(
            ExtractedField extractedField,
            IReadOnlyDictionary<string, object> requestVariables)
        {
            foreach (VariableDefinitionNode variable in
                extractedField.Variables)
            {
                string name = variable.Variable.Name.Value;
                requestVariables.TryGetValue(name, out object value);

                yield return new VariableValue
                (
                    name,
                    variable.Type,
                    value,
                    variable.DefaultValue
                );
            }
        }

        private static void AddVariables(
            ISchema schema,
            NameString schemaName,
            IRemoteQueryRequestBuilder builder,
            DocumentNode query,
            IEnumerable<VariableValue> variableValues)
        {
            OperationDefinitionNode operation =
                query.Definitions.OfType<OperationDefinitionNode>().First();
            var usedVariables = new HashSet<string>(
                operation.VariableDefinitions.Select(t =>
                    t.Variable.Name.Value));

            foreach (VariableValue variableValue in variableValues)
            {
                if (usedVariables.Contains(variableValue.Name))
                {
                    object value = variableValue.Value;

                    if (schema.TryGetType(
                        variableValue.Type.NamedType().Name.Value,
                        out InputObjectType inputType))
                    {
                        value = ObjectVariableRewriter.RewriteVariable(
                            schemaName,
                            WrapType(inputType,
                            variableValue.Type),
                            value);
                    }

                    builder.AddVariableValue(variableValue.Name, value);
                }
            }
        }

        private static IInputType WrapType(
            IInputType namedType,
            ITypeNode typeNode)
        {
            if (typeNode is NonNullTypeNode nntn)
            {
                return new NonNullType(WrapType(namedType, nntn.Type));
            }
            else if (typeNode is ListTypeNode ltn)
            {
                return new ListType(WrapType(namedType, ltn.Type));
            }
            else
            {
                return namedType;
            }
        }

        private static IReadOnlyList<VariableDefinitionNode> CreateVariableDefs(
            IReadOnlyCollection<VariableValue> variableValues)
        {
            var definitions = new List<VariableDefinitionNode>();

            foreach (VariableValue variableValue in variableValues)
            {
                definitions.Add(new VariableDefinitionNode(
                    null,
                    new VariableNode(new NameNode(variableValue.Name)),
                    variableValue.Type,
                    variableValue.DefaultValue
                ));
            }

            return definitions;
        }
    }

}
