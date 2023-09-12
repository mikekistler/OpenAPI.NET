﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. 

using System;
using System.Collections.Generic;
using System.Linq;
using Json.Schema;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Exceptions;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;

namespace Microsoft.OpenApi.Services
{
    /// <summary>
    /// This class is used to walk an OpenApiDocument and convert unresolved references to references to populated objects
    /// </summary>
    public class OpenApiReferenceResolver : OpenApiVisitorBase
    {
        private OpenApiDocument _currentDocument;
        private readonly bool _resolveRemoteReferences;
        private List<OpenApiError> _errors = new List<OpenApiError>();

        /// <summary>
        /// Initializes the <see cref="OpenApiReferenceResolver"/> class.
        /// </summary>
        public OpenApiReferenceResolver(OpenApiDocument currentDocument, bool resolveRemoteReferences = true)
        {
            _currentDocument = currentDocument;
            _resolveRemoteReferences = resolveRemoteReferences;
        }

        /// <summary>
        /// List of errors related to the OpenApiDocument
        /// </summary>
        public IEnumerable<OpenApiError> Errors => _errors;

        /// <summary>
        /// Resolves tags in OpenApiDocument
        /// </summary>
        /// <param name="doc"></param>
        public override void Visit(OpenApiDocument doc)
        {
            if (doc.Tags != null)
            {
                ResolveTags(doc.Tags);
            }
        }

        /// <summary>
        /// Visits the referenceable element in the host document
        /// </summary>
        /// <param name="referenceable">The referenceable element in the doc.</param>
        public override void Visit(IOpenApiReferenceable referenceable)
        {
            if (referenceable.Reference != null)
            {
                referenceable.Reference.HostDocument = _currentDocument;
            }
        }

        /// <summary>
        /// Resolves references in components
        /// </summary>
        /// <param name="components"></param>
        public override void Visit(OpenApiComponents components)
        {
            ResolveMap(components.Parameters);
            ResolveMap(components.RequestBodies);
            ResolveMap(components.Responses);
            ResolveMap(components.Links);
            ResolveMap(components.Callbacks);
            ResolveMap(components.Examples);
            ResolveJsonSchemas(components.Schemas);
            ResolveMap(components.PathItems);
            ResolveMap(components.SecuritySchemes);
            ResolveMap(components.Headers);
        }

        /// <summary>
        /// Resolves all references used in callbacks
        /// </summary>
        /// <param name="callbacks"></param>
        public override void Visit(IDictionary<string, OpenApiCallback> callbacks)
        {
            ResolveMap(callbacks);
        }

        /// <summary>
        /// Resolves all references used in webhooks
        /// </summary>
        /// <param name="webhooks"></param>
        public override void Visit(IDictionary<string, OpenApiPathItem> webhooks)
        {
            ResolveMap(webhooks);
        }

        /// <summary>
        /// Resolve all references used in an operation
        /// </summary>
        public override void Visit(OpenApiOperation operation)
        {
            ResolveObject(operation.RequestBody, r => operation.RequestBody = r);
            ResolveList(operation.Parameters);

            if (operation.Tags != null)
            {
                ResolveTags(operation.Tags);
            }
        }

        /// <summary>
        /// Resolve all references used in mediaType object
        /// </summary>
        /// <param name="mediaType"></param>
        public override void Visit(OpenApiMediaType mediaType)
        {
            ResolveJsonSchema(mediaType.Schema, r => mediaType.Schema = r ?? mediaType.Schema);
        }

        /// <summary>
        /// Resolve all references to examples
        /// </summary>
        /// <param name="examples"></param>
        public override void Visit(IDictionary<string, OpenApiExample> examples)
        {
            ResolveMap(examples);
        }

        /// <summary>
        /// Resolve all references to responses
        /// </summary>
        public override void Visit(OpenApiResponses responses)
        {
            ResolveMap(responses);
        }

        /// <summary>
        /// Resolve all references to headers
        /// </summary>
        /// <param name="headers"></param>
        public override void Visit(IDictionary<string, OpenApiHeader> headers)
        {
            ResolveMap(headers);
        }

        /// <summary>
        /// Resolve all references to SecuritySchemes
        /// </summary>
        public override void Visit(OpenApiSecurityRequirement securityRequirement)
        {
            foreach (var scheme in securityRequirement.Keys.ToList())
            {
                ResolveObject(scheme, (resolvedScheme) =>
                {
                    if (resolvedScheme != null)
                    {
                        // If scheme was unresolved
                        // copy Scopes and remove old unresolved scheme
                        var scopes = securityRequirement[scheme];
                        securityRequirement.Remove(scheme);
                        securityRequirement.Add(resolvedScheme, scopes);
                    }
                });
            }
        }

        /// <summary>
        /// Resolve all references to parameters
        /// </summary>
        public override void Visit(IList<OpenApiParameter> parameters)
        {
            ResolveList(parameters);
        }

        /// <summary>
        /// Resolve all references used in a parameter
        /// </summary>
        public override void Visit(OpenApiParameter parameter)
        {
            ResolveJsonSchema(parameter.Schema, r => parameter.Schema = r);
            ResolveMap(parameter.Examples);
        }

        /// <summary>
        /// Resolve all references to links
        /// </summary>
        public override void Visit(IDictionary<string, OpenApiLink> links)
        {
            ResolveMap(links);
        }
        
        /// <summary>
        /// Resolve all references used in a schem
        /// </summary>
        /// <param name="schema"></param>
        public override void Visit(ref JsonSchema schema)
        {
            var tempSchema = schema;
            var builder = new JsonSchemaBuilder();
            foreach (var keyword in tempSchema.Keywords)
            {
                builder.Add(keyword);
            }
            
            ResolveJsonSchema(schema.GetItems(), r => tempSchema = builder.Items(r));
            ResolveJsonSchemaList((IList<JsonSchema>)schema.GetOneOf());
            ResolveJsonSchemaList((IList<JsonSchema>)schema.GetAllOf());
            ResolveJsonSchemaList((IList<JsonSchema>)schema.GetAnyOf());
            ResolveJsonSchemaMap((IDictionary<string, JsonSchema>)schema.GetProperties());
            ResolveJsonSchema(schema.GetAdditionalProperties(), r => tempSchema = builder.AdditionalProperties(r));

            schema = builder.Build();
        }

        private void ResolveJsonSchemas(IDictionary<string, JsonSchema> schemas)
        {
            foreach (var schema in schemas)
            {
                var schemaValue = schema.Value;
                Visit(ref schemaValue);
            }
        }

        private JsonSchema ResolveJsonSchemaReference(JsonSchema schema)
        {
            var reference = schema.GetRef();
            if (reference == null)
            {
                return schema;
            }
            var refUri = $"http://everything.json{reference.OriginalString.TrimStart('#')}";
            return (JsonSchema)SchemaRegistry.Global.Get(new Uri(refUri));
        }

        /// <summary>
        /// Replace references to tags with either tag objects declared in components, or inline tag object
        /// </summary>
        private void ResolveTags(IList<OpenApiTag> tags)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                if (IsUnresolvedReference(tag))
                {
                    var resolvedTag = ResolveReference<OpenApiTag>(tag.Reference);

                    if (resolvedTag == null)
                    {
                        resolvedTag = new OpenApiTag()
                        {
                            Name = tag.Reference.Id
                        };
                    }
                    tags[i] = resolvedTag;
                }
            }
        }

        private void ResolveObject<T>(T entity, Action<T> assign) where T : class, IOpenApiReferenceable, new()
        {
            if (entity == null) return;

            if (IsUnresolvedReference(entity))
            {
                assign(ResolveReference<T>(entity.Reference));
            }
        }

        private void ResolveJsonSchema(JsonSchema schema, Action<JsonSchema> assign)
        {
            if (schema == null) return;

            if (schema.GetRef() != null)
            {
                assign(ResolveJsonSchemaReference(schema));
            }
        }

        private void ResolveList<T>(IList<T> list) where T : class, IOpenApiReferenceable, new()
        {
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                var entity = list[i];
                if (IsUnresolvedReference(entity))
                {
                    list[i] = ResolveReference<T>(entity.Reference);
                }
            }
        }

        private void ResolveJsonSchemaList(IList<JsonSchema> list)
        {
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                var entity = list[i];
                if (entity.GetRef() != null)
                {
                    list[i] = ResolveJsonSchemaReference(entity);
                }
            }
        }

        private void ResolveMap<T>(IDictionary<string, T> map) where T : class, IOpenApiReferenceable, new()
        {
            if (map == null) return;

            foreach (var key in map.Keys.ToList())
            {
                var entity = map[key];
                if (IsUnresolvedReference(entity))
                {
                    map[key] = ResolveReference<T>(entity.Reference);
                }
            }
        }

        private void ResolveJsonSchemaMap(IDictionary<string, JsonSchema> map)
        {
            if (map == null) return;

            foreach (var key in map.Keys.ToList())
            {
                var entity = map[key];
                if (entity.GetRef() != null)
                {
                    map[key] = ResolveJsonSchemaReference(entity);
                }
            }
        }

        private T ResolveReference<T>(OpenApiReference reference) where T : class, IOpenApiReferenceable, new()
        {
            if (string.IsNullOrEmpty(reference.ExternalResource))
            {
                try
                {
                    return _currentDocument.ResolveReference(reference, false) as T;
                }
                catch (OpenApiException ex)
                {
                    _errors.Add(new OpenApiReferenceError(ex));
                    return null;
                }
            }
            // The concept of merging references with their target at load time is going away in the next major version
            // External references will not support this approach.
            //else if (_resolveRemoteReferences == true)
            //{
            //    if (_currentDocument.Workspace == null)
            //    {
            //        _errors.Add(new OpenApiReferenceError(reference,"Cannot resolve external references for documents not in workspaces."));
            //        // Leave as unresolved reference
            //        return new T()
            //        {
            //            UnresolvedReference = true,
            //            Reference = reference
            //        };
            //    }
            //    var target = _currentDocument.Workspace.ResolveReference(reference);

            //    // TODO:  If it is a document fragment, then we should resolve it within the current context

            //    return target as T;
            //}
            else
            {
                // Leave as unresolved reference
                return new T()
                {
                    UnresolvedReference = true,
                    Reference = reference
                };
            }
        }

        private bool IsUnresolvedReference(IOpenApiReferenceable possibleReference)
        {
            return possibleReference != null && possibleReference.UnresolvedReference;
        }
    }
}
