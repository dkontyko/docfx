// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Microsoft.Docs.Build
{
    internal class InternalXrefMapBuilder
    {
        private readonly ErrorLog _errorLog;
        private readonly TemplateEngine _templateEngine;
        private readonly DocumentProvider _documentProvider;
        private readonly MetadataProvider _metadataProvider;
        private readonly MonikerProvider _monikerProvider;
        private readonly Input _input;
        private readonly BuildScope _buildScope;
        private readonly JsonSchemaTransformer _jsonSchemaTransformer;

        public InternalXrefMapBuilder(
            ErrorLog errorLog,
            TemplateEngine templateEngine,
            DocumentProvider documentProvider,
            MetadataProvider metadataProvider,
            MonikerProvider monikerProvider,
            Input input,
            BuildScope buildScope,
            JsonSchemaTransformer jsonSchemaTransformer)
        {
            _errorLog = errorLog;
            _templateEngine = templateEngine;
            _documentProvider = documentProvider;
            _metadataProvider = metadataProvider;
            _monikerProvider = monikerProvider;
            _input = input;
            _buildScope = buildScope;
            _jsonSchemaTransformer = jsonSchemaTransformer;
        }

        public IReadOnlyDictionary<string, InternalXrefSpec> Build()
        {
            var builder = new ListBuilder<InternalXrefSpec>();

            using (Progress.Start("Building Xref map"))
            {
                ParallelUtility.ForEach(
                    _errorLog,
                    _buildScope.GetFiles(ContentType.Page),
                    file => Load(builder, file));
            }

            var xrefmap =
                from spec in builder.ToList()
                group spec by spec.Uid.Value into g
                let uid = g.Key
                let spec = AggregateXrefSpecs(uid, g.ToArray())
                select (uid, spec);

            var result = xrefmap.ToDictionary(item => item.uid, item => item.spec);
            result.TrimExcess();

            return result;
        }

        private void Load(
            ListBuilder<InternalXrefSpec> xrefs,
            FilePath path)
        {
            var file = _documentProvider.GetDocument(path);
            if (file.ContentType != ContentType.Page)
            {
                return;
            }

            var errors = new List<Error>();
            switch (file.FilePath.Format)
            {
                case FileFormat.Markdown:
                    {
                        var (fileMetaErrors, fileMetadata) = _metadataProvider.GetMetadata(file.FilePath);
                        errors.AddRange(fileMetaErrors);
                        var (markdownErrors, spec) = LoadMarkdown(fileMetadata, file);
                        errors.AddRange(markdownErrors);
                        if (spec != null)
                        {
                            xrefs.Add(spec);
                        }
                        break;
                    }
                case FileFormat.Yaml:
                    {
                        var (yamlErrors, token) = _input.ReadYaml(file.FilePath);
                        errors.AddRange(yamlErrors);
                        var (schemaErrors, specs) = LoadSchemaDocument(token, file);
                        errors.AddRange(schemaErrors);
                        xrefs.AddRange(specs);
                        break;
                    }
                case FileFormat.Json:
                    {
                        var (jsonErrors, token) = _input.ReadJson(file.FilePath);
                        errors.AddRange(jsonErrors);
                        var (schemaErrors, specs) = LoadSchemaDocument(token, file);
                        errors.AddRange(schemaErrors);
                        xrefs.AddRange(specs);
                        break;
                    }
            }
            _errorLog.Write(errors);
        }

        private (List<Error> errors, InternalXrefSpec? spec) LoadMarkdown(UserMetadata metadata, Document file)
        {
            if (string.IsNullOrEmpty(metadata.Uid))
            {
                return (new List<Error>(), default);
            }

            var (errors, monikers) = _monikerProvider.GetFileLevelMonikers(file.FilePath);
            var xref = new InternalXrefSpec(metadata.Uid, file.SiteUrl, file, monikers);

            xref.XrefProperties["name"] = new Lazy<JToken>(() => new JValue(string.IsNullOrEmpty(metadata.Title) ? metadata.Uid : metadata.Title.Value));

            return (errors, xref);
        }

        private (List<Error> errors, IReadOnlyList<InternalXrefSpec> specs) LoadSchemaDocument(
            JToken token,
            Document file)
        {
            var schemaTemplate = _templateEngine.GetSchema(file.Mime);

            return _jsonSchemaTransformer.LoadXrefSpecs(schemaTemplate.JsonSchema, file, token);
        }

        private InternalXrefSpec AggregateXrefSpecs(string uid, InternalXrefSpec[] specsWithSameUid)
        {
            // no conflicts
            if (specsWithSameUid.Length == 1)
            {
                return specsWithSameUid.First();
            }

            // multiple uid conflicts without moniker range definition
            // log an warning and take the first one order by the declaring file
            var duplicatedSpecs = specsWithSameUid.Where(item => item.Monikers.Count == 0).ToArray();
            if (duplicatedSpecs.Length > 1)
            {
                var duplicatedSources = (from spec in duplicatedSpecs where spec.Uid.Source != null select spec.Uid.Source).ToArray();
                foreach (var spec in duplicatedSpecs)
                {
                    _errorLog.Write(Errors.Xref.DuplicateUid(spec.Uid, duplicatedSources));
                }
            }

            // uid conflicts with overlapping monikers
            // log an warning and take the first one order by the declaring file
            var conflictsWithMoniker = specsWithSameUid.Where(x => x.Monikers.Count > 0).ToArray();
            if (CheckOverlappingMonikers(specsWithSameUid, out var overlappingMonikers))
            {
                _errorLog.Write(Errors.Versioning.MonikerOverlapping(uid, specsWithSameUid.Select(spec => spec.DeclaringFile).ToList(), overlappingMonikers));
            }

            // uid conflicts with different values of the same xref property
            // log an warning and take the first one order by the declaring file
            var xrefProperties = specsWithSameUid.SelectMany(x => x.XrefProperties.Keys).Distinct();
            foreach (var xrefProperty in xrefProperties)
            {
                var conflictingNames = specsWithSameUid.Select(x => x.GetXrefPropertyValueAsString(xrefProperty)).Distinct();
                if (conflictingNames.Count() > 1)
                {
                    _errorLog.Write(Errors.Xref.UidPropertyConflict(uid, xrefProperty, conflictingNames));
                }
            }

            return specsWithSameUid.OrderBy(spec => spec.DeclaringFile).First();
        }

        private bool CheckOverlappingMonikers(IXrefSpec[] specsWithSameUid, out HashSet<string> overlappingMonikers)
        {
            var isOverlapping = false;
            overlappingMonikers = new HashSet<string>();
            var monikerHashSet = new HashSet<string>();
            foreach (var spec in specsWithSameUid)
            {
                foreach (var moniker in spec.Monikers)
                {
                    if (!monikerHashSet.Add(moniker))
                    {
                        overlappingMonikers.Add(moniker);
                        isOverlapping = true;
                    }
                }
            }
            return isOverlapping;
        }
    }
}