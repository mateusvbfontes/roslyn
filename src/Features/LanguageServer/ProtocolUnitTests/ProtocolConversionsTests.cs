﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests
{
    public class ProtocolConversionsTests
    {
        [Fact]
        public void CompletionItemKind_DoNotUseMethodAndFunction()
        {
            var map = ProtocolConversions.RoslynTagToCompletionItemKinds;
            var containsMethod = map.Values.Any(c => c.Contains(CompletionItemKind.Method));
            var containsFunction = map.Values.Any(c => c.Contains(CompletionItemKind.Function));

            Assert.False(containsFunction && containsMethod, "Don't use Method and Function completion item kinds as it causes user confusion.");
        }
    }
}
