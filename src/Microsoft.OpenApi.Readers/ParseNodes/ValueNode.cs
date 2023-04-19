﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. 

using System.Text.Json.Nodes;
using Microsoft.OpenApi.Readers.Exceptions;

namespace Microsoft.OpenApi.Readers.ParseNodes
{
    internal class ValueNode : ParseNode
    {
        private readonly JsonValue _node;

        public ValueNode(ParsingContext context, JsonNode node) : base(
            context)
        {
            if (node is not JsonValue scalarNode)
            {
                throw new OpenApiReaderException("Expected a value.", node);
            }
            _node = scalarNode;
        }

        public override string GetScalarValue() => _node.GetValue<string>();

        /// <summary>
        /// Create a <see cref="JsonNode"/>
        /// </summary>
        /// <returns>The created Any object.</returns>
        public override JsonNode CreateAny()
        {
            var value = GetScalarValue();
            return value;
        }
    }
}
