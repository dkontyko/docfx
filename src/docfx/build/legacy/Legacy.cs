// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Docs.Build
{
    internal static class Legacy
    {
        public static void ConvertToLegacyModel(
            string docsetPath,
            Context context,
            Dictionary<FilePath, PublishItem> fileManifests,
            DependencyMap dependencyMap)
        {
            using (Progress.Start("Converting to legacy"))
            {
                var documents = fileManifests.Where(f => !f.Value.HasError).ToDictionary(
                    k => context.DocumentProvider.GetDocument(k.Key), v => v.Value);

                LegacyManifest.Convert(docsetPath, context, documents);
                var legacyDependencyMap = LegacyDependencyMap.Convert(docsetPath, context, dependencyMap);

                // TODO: remove this since file map link is deprecated
                LegacyFileMap.Convert(context, legacyDependencyMap, documents);
            }
        }
    }
}