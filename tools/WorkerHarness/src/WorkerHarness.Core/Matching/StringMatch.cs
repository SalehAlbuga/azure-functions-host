﻿using Microsoft.Azure.Functions.WorkerHarness.Grpc.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WorkerHarness.Core
{
    /// <summary>
    /// provide matching service that uses string comparison
    /// </summary>
    public class StringMatch : IMatch
    {
        private readonly string objectVariablePattern = @"^(((\$\{)([^\{\}]+)(\}\.*))|(\$\.))";

        public bool Match(MatchingContext match, object source)
        {
            // find all properties to index in match.query
            string replacement = Regex.Replace(match.Query, objectVariablePattern, string.Empty);
            string[] properties = replacement.Split(".");

            // convert the actual object to a JsonNode
            JsonSerializerOptions options = new();
            options.Converters.Add(new JsonStringEnumConverter());
            string jsonString = JsonSerializer.Serialize(source, options);
            JsonNode jsonNode = JsonNode.Parse(jsonString) ?? throw new InvalidOperationException($"Unable to convert a string to a JsonNode object");

            // recursively index into jsonNode to find the actual string data
            string actual = VariableHelper.RecursiveIndex(jsonNode, properties, 0).GetValue<string>() ??
                throw new InvalidDataException($"Unable to find a string value for the query {match.Query}");

            // if the expected value in a match still has unresolved dependency, return false
            if (!match.TryEvaluate(out string? expected))
            {
                return false;
            }

            return actual.Equals(expected);
        }

        public bool MatchAll(IEnumerable<MatchingContext> matches, object source)
        {
            foreach (var match in matches)
            {
                if (!Match(match, source))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
