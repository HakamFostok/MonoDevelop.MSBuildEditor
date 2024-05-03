// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.Extensions.Logging;

using MonoDevelop.MSBuild.Analysis;
using MonoDevelop.MSBuild.Language.Expressions;
using MonoDevelop.MSBuild.Language.Syntax;
using MonoDevelop.MSBuild.Language.Typesystem;
using MonoDevelop.MSBuild.Schema;
using MonoDevelop.MSBuild.Workspace;

using MonoDevelop.Xml.Dom;
using MonoDevelop.Xml.Logging;
using MonoDevelop.Xml.Parser;

using AttributeName = MonoDevelop.MSBuild.Language.Syntax.MSBuildAttributeName;
using ElementName = MonoDevelop.MSBuild.Language.Syntax.MSBuildElementName;
using DiagnosticProperty = MonoDevelop.MSBuild.Language.CoreDiagnosticProperty;

namespace MonoDevelop.MSBuild.Language
{
	partial class MSBuildDocumentValidator : MSBuildDocumentVisitor
	{
		public MSBuildDocumentValidator (MSBuildDocument document, ITextSource textSource, ILogger logger) : base (document, textSource, logger)
		{
			if (Document.ProjectElement is null) {
				Document.Diagnostics.Add (CoreDiagnostics.MissingProjectElement, new TextSpan (0, 0));
			}
		}

		IEnumerable<IMSBuildSchema> GetSchemasExcludingCurrentDocInferred () => Document.GetSchemas (skipThisDocumentInferredSchema: true);

		protected override void VisitUnknownElement (XElement element)
		{
			Document.Diagnostics.Add (CoreDiagnostics.UnknownElement, element.Span, element.Name.FullName);
			base.VisitUnknownElement (element);
		}

		protected override void VisitUnknownAttribute (XElement element, XAttribute attribute)
		{
			Document.Diagnostics.Add (CoreDiagnostics.UnknownAttribute, attribute.Span, attribute.Name.FullName);
			base.VisitUnknownAttribute (element, attribute);
		}

		protected override void VisitResolvedElement (XElement element, MSBuildElementSyntax elementSyntax, ITypedSymbol elementSymbol)
		{
			try {
				ValidateResolvedElement (element, elementSyntax, elementSymbol);

				if (element.IsComplete && elementSyntax.SyntaxKind != MSBuildSyntaxKind.TaskBody) {
					base.VisitResolvedElement (element, elementSyntax, elementSymbol);
				}

			} catch (Exception ex) when (!(ex is OperationCanceledException && CancellationToken.IsCancellationRequested)) {
				Document.Diagnostics.Add (CoreDiagnostics.InternalError, element.NameSpan, ex.Message);
				Logger.LogInternalException (ex, nameof (MSBuildDocumentValidator));
			}
		}

		void ValidateResolvedElement (XElement element, MSBuildElementSyntax elementSyntax, ITypedSymbol elementSymbol)
		{
			CheckDeprecated (elementSyntax, element);

			if (elementSymbol != elementSyntax && elementSymbol is IVersionableSymbol versionableSymbol) {
				CheckDeprecated (versionableSymbol, element);
			}

			foreach (var rat in elementSyntax.Attributes) {
				if (rat.Required && !rat.IsAbstract) {
					var xat = element.Attributes.Get (rat.Name, true);
					if (xat == null) {
						Document.Diagnostics.Add (CoreDiagnostics.MissingRequiredAttribute, element.NameSpan, element.Name, rat.Name);
					}
				}
			}

			TextSpan[] GetNameSpans (XElement el) => (el.ClosingTag is XClosingTag ct)
				? [element.NameSpan, new TextSpan (ct.Span.Start + 2, ct.Name.Length)]
				: [element.NameSpan];

			switch (elementSyntax.SyntaxKind) {
			case MSBuildSyntaxKind.Project:
				if (Document.FileKind.IsProject ()) {
					ValidateProjectHasTarget (element);
				}
				break;
			case MSBuildSyntaxKind.OnError:
				ValidateOnErrorOnlyFollowedByOnError (element);
				break;
			case MSBuildSyntaxKind.Otherwise:
				ValidateOtherwiseIsLastElement (element);
				break;
			case MSBuildSyntaxKind.Output:
				ValidateOutputHasPropertyOrItemName (element);
				break;
			case MSBuildSyntaxKind.UsingTask:
				ValidateUsingTask (element);
				break;
			case MSBuildSyntaxKind.Import:
				ValidateImportOrSdkAttributes (element, isSdkElement: false);
				break;
			case MSBuildSyntaxKind.Item:
				ValidateItemAttributes (elementSyntax, element);

				// TODO: reuse the existing resolved symbol
				if (!IsItemUsed (element.Name.Name, ReferenceUsage.Read, out _)) {
					Document.Diagnostics.Add (
						CoreDiagnostics.UnreadItem,
						element.NameSpan,
						ImmutableDictionary<string, object>.Empty
							.Add (DiagnosticProperty.MisspelledNameOrValue, element.Name.Name)
							.Add (DiagnosticProperty.MisspelledNameSpans, GetNameSpans (element)),
						element.Name.Name
					);
				}
				break;

			case MSBuildSyntaxKind.Task:
				ValidateTaskParameters (elementSyntax, element);
				break;

			case MSBuildSyntaxKind.Property:
				// TODO: reuse the existing resolved symbol
				if (!IsPropertyUsed (element.Name.Name, ReferenceUsage.Read, out _)) {
					Document.Diagnostics.Add (
						CoreDiagnostics.UnreadProperty,
						element.NameSpan,
						ImmutableDictionary<string, object>.Empty
							.Add (DiagnosticProperty.MisspelledNameOrValue, element.Name.Name)
							.Add (DiagnosticProperty.MisspelledNameSpans, GetNameSpans (element)),
						element.Name.Name
					);
				}
				if (elementSymbol is PropertyInfo property) {
					CheckPropertyWrite (property, element.NameSpan);
				}
				break;

			case MSBuildSyntaxKind.Metadata:
				if ((element.Parent as XElement)?.Name.Name is not string metaItem) {
					break;
				}

				// TODO: reuse the existing resolved symbol
				if (!IsMetadataUsed (metaItem, element.Name.Name, ReferenceUsage.Read, out _)) {
					Document.Diagnostics.Add (
						CoreDiagnostics.UnreadMetadata,
						element.NameSpan,
						ImmutableDictionary<string, object>.Empty
							.Add (DiagnosticProperty.MisspelledNameOrValue, element.Name.Name)
							.Add (DiagnosticProperty.MisspelledNameSpans, GetNameSpans (element))
							.Add (DiagnosticProperty.MisspelledMetadataItemName, metaItem),
						metaItem, element.Name.Name
					);
				}
				break;
			case MSBuildSyntaxKind.Sdk:
				if (element.Parent is XElement parent) {
					foreach (var el in parent.Elements) {
						if (el == element) {
							break;
						}
						if (!el.Name.Equals (ElementName.Sdk, true)) {
							Document.Diagnostics.Add (CoreDiagnostics.SdkElementAfterNonSdkElement, element.NameSpan);
							break;
						}
					}
				}
				ValidateImportOrSdkAttributes (element, isSdkElement: true);
				break;
			}

			if (elementSyntax.ValueKind == MSBuildValueKind.Nothing) {
				foreach (var txt in element.Nodes.OfType<XText> ()) {
					Document.Diagnostics.Add (CoreDiagnostics.UnexpectedText, txt.Span, element.Name.Name);
				}
			}
		}

		bool CheckDeprecated (IVersionableSymbol versionableSymbol, INamedXObject namedObj) => CheckDeprecated (versionableSymbol, namedObj.NameSpan);

		bool CheckDeprecated (IVersionableSymbol versionableSymbol, ExpressionNode expressionNode) => CheckDeprecated (versionableSymbol, expressionNode.Span);

		bool CheckDeprecated (IVersionableSymbol versionableSymbol, TextSpan squiggleSpan)
		{
			if (versionableSymbol.IsDeprecated (out string? deprecationMessage)) {
				Document.Diagnostics.Add (
					CoreDiagnostics.DeprecatedWithMessage,
					squiggleSpan,
					DescriptionFormatter.GetKindNoun (versionableSymbol),
					versionableSymbol.Name,
					deprecationMessage
				);
				return true;
			}
			return false;
		}

		void ValidateProjectHasTarget (XElement element)
		{
			if (element.Attributes.Get (AttributeName.Sdk, true) != null) {
				return;
			}

			foreach (var child in element.Nodes) {
				if (child is XElement projectChild && projectChild.IsNamed && !projectChild.Name.HasPrefix) {
					if (projectChild.Name.Equals (ElementName.Target, true) || projectChild.Name.Equals (ElementName.Import, true)) {
						return;
					}
				}
			}

			Document.Diagnostics.Add (CoreDiagnostics.NoTargets, element.NameSpan);
		}

		void ValidateOnErrorOnlyFollowedByOnError (XElement element)
		{
			var nextSibling = element.GetNextSiblingElement ();
			if (nextSibling != null && !nextSibling.Name.Equals (ElementName.OnError, true)) {
				Document.Diagnostics.Add (CoreDiagnostics.OnErrorMustBeLastInTarget, element.GetNextSiblingElement ().NameSpan);
			}
		}

		void ValidateOtherwiseIsLastElement (XElement element)
		{
			if (element.GetNextSiblingElement () != null) {
				Document.Diagnostics.Add (CoreDiagnostics.OtherwiseMustBeLastInChoose, element.GetNextSiblingElement ().NameSpan);
			}
		}

		void ValidateOutputHasPropertyOrItemName (XElement element)
		{
			bool foundItemOrPropertyName = false;
			foreach (var att in element.Attributes) {
				if (att.Name.Equals (AttributeName.ItemName, true) || att.Name.Equals (AttributeName.PropertyName, true)) {
					foundItemOrPropertyName = true;
					break;
				}
			}
			if (!foundItemOrPropertyName) {
				Document.Diagnostics.Add (CoreDiagnostics.OutputMustHavePropertyOrItemName, element.NameSpan);
			}
		}

		void ValidateUsingTask (XElement element)
		{
			XAttribute taskFactoryAtt = null;
			XAttribute asmNameAtt = null;
			XAttribute asmFileAtt = null;
			XAttribute taskNameAtt = null;

			foreach (var att in element.Attributes) {
				if (att.Name.Equals (AttributeName.AssemblyFile, true)) {
					asmFileAtt = att;
				} else if (att.Name.Equals (AttributeName.AssemblyName, true)) {
					asmNameAtt = att;
				} else if (att.Name.Equals (AttributeName.TaskFactory, true)) {
					taskFactoryAtt = att;
				} else if (att.Name.Equals (AttributeName.TaskName, true)) {
					taskNameAtt = att;
				}
			}

			// ValidateResolvedElement will check for the existence of the TaskName as it's marked as Required
			if (asmNameAtt == null && asmFileAtt == null) {
				Document.Diagnostics.Add (CoreDiagnostics.UsingTaskMustHaveAssembly, element.NameSpan);
			} else if (taskFactoryAtt != null && asmNameAtt != null) {
				Document.Diagnostics.Add (CoreDiagnostics.TaskFactoryCannotHaveAssemblyName, asmNameAtt.NameSpan);
			} else if (taskFactoryAtt != null && asmFileAtt == null) {
				Document.Diagnostics.Add (CoreDiagnostics.TaskFactoryMustHaveAssemblyFile, element.NameSpan);
			} else if (asmNameAtt != null && asmFileAtt != null) {
				Document.Diagnostics.Add (CoreDiagnostics.TaskFactoryMustHaveOneAssemblyOnly, element.NameSpan);
			}

			XElement parameterGroup = null, taskBody = null;
			foreach (var child in element.Elements) {
				if (child.Name.Equals (ElementName.ParameterGroup, true)) {
					if (parameterGroup != null) {
						Document.Diagnostics.Add (CoreDiagnostics.OneParameterGroup, child.NameSpan);
					}
					if (taskBody != null) {
						Document.Diagnostics.Add (CoreDiagnostics.OneTaskBody, child.NameSpan);
					}
					parameterGroup = child;
				} else if (child.Name.Equals (ElementName.Task, true)) {
					taskBody = child;
				}
			}

			bool isFactoryBased = taskFactoryAtt is not null || parameterGroup is not null || taskBody is not null;

			if (isFactoryBased) {
				if (taskBody is not null && taskFactoryAtt is null) {
					Document.Diagnostics.Add (CoreDiagnostics.TaskBodyMustHaveFactory, taskBody.NameSpan);
				}
				if (parameterGroup is not null && taskFactoryAtt is null) {
					Document.Diagnostics.Add (CoreDiagnostics.ParameterGroupMustHaveFactory, parameterGroup.NameSpan);
				}
				if (taskBody is null && taskFactoryAtt is not null) {
					Document.Diagnostics.Add (CoreDiagnostics.TaskFactoryMustHaveBody, element.NameSpan);
				}

				if (taskFactoryAtt is not null && taskFactoryAtt.TryGetValue (out var taskFactoryName) && taskFactoryName.Length > 0) {
					switch (WellKnownTaskFactory.TryGet (taskFactoryName, asmFileAtt?.Value)) {
					case WellKnownTaskFactory.RoslynCodeTaskFactory:
						if (taskBody is not null) {
							ValidateRoslynCodeTaskFactory (element, taskBody, parameterGroup);
						}
						break;
					case null:
						Document.Diagnostics.Add (CoreDiagnostics.UnknownTaskFactory, taskFactoryAtt.ValueSpan.Value, taskFactoryName);
						break;
					default:
						// known but we don't have any special handling
						break;
					}
				}
			}

			if (taskNameAtt is null || taskNameAtt.Value is not string fullTaskName) {
				return;
			}

			if (!TaskInfo.ValidateTaskName (fullTaskName, out string taskName, out string taskNamespace)) {
				Document.Diagnostics.Add (CoreDiagnostics.InvalidTaskName, element.NameSpan);
				return;
			}

			if (!isFactoryBased) {
				if (Document.GetSchemas ().GetTask (taskName).DeclarationKind == TaskDeclarationKind.AssemblyUnresolved) {
					Document.Diagnostics.Add (CoreDiagnostics.TaskDefinitionNotResolvedFromAssembly, element.NameSpan, taskName);
				}

				if (taskNamespace is null) {
					Document.Diagnostics.Add (CoreDiagnostics.FullyQualifiedTaskName, element.NameSpan, taskName);
				}
			}
		}

		void ValidateRoslynCodeTaskFactory (XElement usingTask, XElement taskBody, XElement parameterGroup)
		{
			var code = taskBody.Elements.FirstOrDefault (f => string.Equals (f.Name.Name, "code", StringComparison.OrdinalIgnoreCase));
			if (code == null) {
				Document.Diagnostics.Add (CoreDiagnostics.RoslynCodeTaskFactoryRequiresCodeElement, taskBody.NameSpan);
				return;
			}
			var typeAtt = code.Attributes.Get ("Type", true);
			var sourceAtt = code.Attributes.Get ("Source", true);
			if (sourceAtt != null || string.Equals (typeAtt?.Value, "Class", StringComparison.OrdinalIgnoreCase)) {
				if (parameterGroup != null) {
					Document.Diagnostics.Add (CoreDiagnostics.RoslynCodeTaskFactoryWithClassIgnoresParameterGroup, parameterGroup.NameSpan);
				}
			}
		}

		void ValidateImportOrSdkAttributes (XElement element, bool isSdkElement)
		{
			XAttribute? versionAtt = null, minVersionAtt = null, sdkAtt = null;
			foreach (var att in element.Attributes) {
				if (att.Name.Equals (AttributeName.Version, true)) {
					versionAtt = att;
				} else if (att.Name.Equals (AttributeName.MinimumVersion, true)) {
					minVersionAtt = att;
				} else if (!isSdkElement && att.Name.Equals (AttributeName.Sdk, true)) {
					sdkAtt = att;
				}
			}

			if (!isSdkElement && sdkAtt is null) {
				if (minVersionAtt is not null) {
					Document.Diagnostics.Add (CoreDiagnostics.ImportMinimumVersionRequiresSdk, minVersionAtt.NameSpan);
				}
				if (versionAtt is not null) {
					Document.Diagnostics.Add (CoreDiagnostics.ImportVersionRequiresSdk, versionAtt.NameSpan);
				}
			}

			if (minVersionAtt is not null && versionAtt is not null) {
				Document.Diagnostics.Add (CoreDiagnostics.RedundantMinimumVersion, minVersionAtt.NameSpan);
			}
		}

		void ValidateItemAttributes (MSBuildElementSyntax resolved, XElement element)
		{
			bool isInTarget = resolved.IsInTarget (element);
			bool hasInclude = false, hasUpdate = false, hasRemove = false;
			foreach (var att in element.Attributes) {
				hasInclude |= att.Name.Equals (AttributeName.Include, true);
				hasRemove |= att.Name.Equals (AttributeName.Remove, true);
				if (att.Name.Equals (AttributeName.Update, true)) {
					hasUpdate = true;
					if (isInTarget) {
						Document.Diagnostics.Add (CoreDiagnostics.ItemAttributeNotValidInTarget, att.NameSpan, att.Name.Name);
					}
				}
				if (att.Name.Equals (AttributeName.KeepMetadata, true) || att.Name.Equals (AttributeName.RemoveMetadata, true) || att.Name.Equals (AttributeName.KeepDuplicates, true)) {
					if (!isInTarget) {
						Document.Diagnostics.Add (CoreDiagnostics.ItemAttributeOnlyValidInTarget, att.NameSpan, att.Name.Name);
					}
				}
			}

			if (!hasInclude && !hasRemove && !hasUpdate && !isInTarget) {
				Document.Diagnostics.Add (CoreDiagnostics.ItemMustHaveInclude, element.NameSpan);
			}
		}

		void ValidateTaskParameters (MSBuildElementSyntax resolvedElement, XElement element)
		{
			var info = Document.GetSchemas ().GetTask (element.Name.Name);
			if (info.DeclarationKind == TaskDeclarationKind.Inferred) {
				Document.Diagnostics.Add (CoreDiagnostics.TaskNotDefined, element.NameSpan, element.Name.Name);
				return;
			}

			if (info.DeclarationKind == TaskDeclarationKind.AssemblyUnresolved) {
				Document.Diagnostics.Add (CoreDiagnostics.TaskDefinedButUnresolved, element.NameSpan, element.Name.Name);
				return;
			}

			var required = new HashSet<string> ();
			foreach (var p in info.Parameters) {
				if (p.Value.IsRequired) {
					required.Add (p.Key);
				}
			}

			foreach (var att in element.Attributes) {
				if (!resolvedElement.GetAttribute (att)?.IsAbstract ?? false) {
					continue;
				}
				if (!info.Parameters.TryGetValue (att.Name.Name, out TaskParameterInfo pi)) {
					Document.Diagnostics.Add (CoreDiagnostics.UnknownTaskParameter, att.NameSpan, element.Name.Name, att.Name.Name);
					continue;
				}
				if (pi.IsRequired) {
					required.Remove (pi.Name);
					if (string.IsNullOrWhiteSpace (att.Value)) {
						Document.Diagnostics.Add (CoreDiagnostics.EmptyRequiredTaskParameter, att.NameSpan, element.Name.Name, att.Name.Name);
					}
				}
			}

			foreach (var r in required) {
				Document.Diagnostics.Add (CoreDiagnostics.MissingRequiredTaskParameter, element.NameSpan, element.Name.Name, r);
			}

			foreach (var child in element.Elements) {
				if (child.Name.Equals (ElementName.Output, true)) {
					var paramNameAtt = child.Attributes.Get (AttributeName.TaskParameter, true);
					if (!paramNameAtt.TryGetValue (out string paramName) || paramName.Length == 0) {
						continue;
					}
					if (!info.Parameters.TryGetValue (paramName, out TaskParameterInfo pi)) {
						Document.Diagnostics.Add (CoreDiagnostics.UnknownTaskParameter, paramNameAtt.ValueSpan.Value, element.Name.Name, paramName);
						continue;
					}
					if (!pi.IsOutput) {
						Document.Diagnostics.Add (CoreDiagnostics.NonOutputTaskParameter, paramNameAtt.ValueSpan.Value, element.Name.Name, paramName);
						continue;
					}
				}
			}
		}

		protected override void VisitResolvedAttribute (
			XElement element, XAttribute attribute,
			MSBuildElementSyntax elementSyntax, MSBuildAttributeSyntax attributeSyntax,
			ITypedSymbol elementSymbol, ITypedSymbol attributeSymbol)
		{
			if (attributeSyntax.SyntaxKind == MSBuildSyntaxKind.Item_Metadata) {
				// TODO: reuse the existing resolved symbol
				if (!IsMetadataUsed (element.Name.Name, attribute.Name.Name, ReferenceUsage.Read, out _)) {
					Document.Diagnostics.Add (
						CoreDiagnostics.UnreadMetadata,
						attribute.NameSpan,
						ImmutableDictionary<string, object>.Empty
							.Add (DiagnosticProperty.MisspelledMetadataItemName, element.Name.Name)
							.Add (DiagnosticProperty.MisspelledNameOrValue, attribute.Name.Name),
						element.Name.Name, attribute.Name.Name
					);
				}
			}

			ValidateAttribute (element, attribute, attributeSyntax, attributeSymbol);

			base.VisitResolvedAttribute (element, attribute, elementSyntax, attributeSyntax, elementSymbol, attributeSymbol);
		}

		void ValidateAttribute (XElement element, XAttribute attribute, MSBuildAttributeSyntax attributeSyntax, ITypedSymbol attributeSymbol)
		{
			CheckDeprecated (attributeSyntax, attribute);

			if (attributeSymbol != attributeSyntax && attributeSymbol is IVersionableSymbol versionableSymbol) {
				CheckDeprecated (versionableSymbol, attribute);
			}

			if (string.IsNullOrWhiteSpace (attribute.Value)) {
				if (attributeSyntax.Required) {
					Document.Diagnostics.Add (CoreDiagnostics.RequiredAttributeEmpty, attribute.NameSpan, attribute.Name);
				} else {
					Document.Diagnostics.Add (CoreDiagnostics.AttributeEmpty, attribute.NameSpan, attribute.Name);
				}
				return;
			}
		}

		// the expression with more options enabled so that we can warn if the user is doing something likely invalid
		protected override ExpressionOptions GetExpressionParseOptions (MSBuildValueKind inferredKind)
			=> inferredKind.GetExpressionOptions () | ExpressionOptions.ItemsMetadataAndLists;

		protected override void VisitValue (
			XElement element, XAttribute attribute,
			MSBuildElementSyntax elementSyntax, MSBuildAttributeSyntax? attributeSyntax,
			ITypedSymbol elementSymbol, ITypedSymbol? attributeSymbol,
			string expressionText, ExpressionNode expression)
		{
			ITypedSymbol valueSymbol = attributeSymbol ?? elementSymbol;

			if (Document.FileKind.IsProject () && valueSymbol is IHasDefaultValue hasDefault) {
				if (hasDefault.DefaultValue != null && string.Equals (hasDefault.DefaultValue, expressionText, StringComparison.OrdinalIgnoreCase)) {
					Document.Diagnostics.Add (
						CoreDiagnostics.HasDefaultValue, attribute?.Span ?? element.OuterSpan,
						ImmutableDictionary<string, object>.Empty.Add (DiagnosticProperty.Symbol, valueSymbol),
						DescriptionFormatter.GetTitleCaseKindNoun (valueSymbol), valueSymbol.Name, hasDefault.DefaultValue);
				}
			}

			bool allowExpressions = valueSymbol.AllowsExpressions ();
			bool allowLists = valueSymbol.AllowsLists (MSBuildValueKind.ListSemicolonOrComma);
			MSBuildValueKind kindWithoutModifiers = valueSymbol.ValueKindWithoutModifiers ();

			if (expression is ListExpression list) {
				if (!allowLists) {
					Document.Diagnostics.Add (
					CoreDiagnostics.UnexpectedList,
					new TextSpan (list.Nodes[0].End, list.End - list.Nodes[0].End),
					ImmutableDictionary<string, object>.Empty.Add (DiagnosticProperty.Symbol, valueSymbol),
					DescriptionFormatter.GetKindNoun (valueSymbol),
					valueSymbol.Name);
				} else {
					foreach (var listVal in list.Nodes) {
						if (listVal is ExpressionText listValText) {
							VisitPureLiteral (elementSyntax, attributeSyntax, valueSymbol, listValText);
						}
					}
				}
				if (!allowExpressions) {
					var expr = list.Nodes.FirstOrDefault (n => !(n is ExpressionText));
					if (expr != null) {
						AddExpressionWarning (expr);
					}
				}
			} else if (expression is ExpressionText lit) {
				VisitPureLiteral (elementSyntax, attributeSyntax, valueSymbol, lit);
			} else {
				if (!allowExpressions) {
					AddExpressionWarning (expression);
				}
			}

			foreach (var n in expression.WithAllDescendants ()) {
				switch (n) {
				case ExpressionError err:
					var (desc, args) = ExpressionDiagnostics.GetExpressionError (err, valueSymbol);
					Document.Diagnostics.Add (desc, new TextSpan (err.Offset, Math.Max (1, err.Length)), args);
					break;
				case ExpressionMetadata meta:
					var metaItem = meta.GetItemName ();
					if (string.IsNullOrEmpty (metaItem)) {
						break;
					}

					if (!IsMetadataUsed (metaItem, meta.MetadataName, ReferenceUsage.Write, out var resolvedMetadata)) {
						if (Document.FileKind.IsProject ()) {
							Document.Diagnostics.Add (
								CoreDiagnostics.UnwrittenMetadata,
								meta.Span,
								ImmutableDictionary<string, object>.Empty
									.Add (DiagnosticProperty.MisspelledMetadataItemName, metaItem)
									.Add (DiagnosticProperty.MisspelledNameOrValue, meta.MetadataName)
									.Add (DiagnosticProperty.MisspelledNameSpans, new[] { new TextSpan (meta.MetadataNameOffset, meta.MetadataName.Length) }),
								metaItem, meta.MetadataName
							);
						}
					}
					if (resolvedMetadata is not null) {
						CheckDeprecated (resolvedMetadata, meta.MetadataNameSpan);
					}
					break;
				case ExpressionPropertyName prop:
					if (!IsPropertyUsed (prop.Name, ReferenceUsage.Write, out var resolvedProperty)) {
						if (Document.FileKind.IsProject ()) {
							AddMisspelledNameError (CoreDiagnostics.UnwrittenProperty, prop.Name, prop.Span, prop.Name);
						}
					}
					if (resolvedProperty is not null) {
						CheckDeprecated (resolvedProperty, prop);
					}
					break;
				case ExpressionItemName item:
					if (!IsItemUsed (item.Name, ReferenceUsage.Write, out var resolvedItem)) {
						if (Document.FileKind.IsProject ()) {
							AddMisspelledNameError (CoreDiagnostics.UnwrittenItem, item.Name, item.Span, item.Name);
						}
					}
					if (resolvedItem is not null) {
						CheckDeprecated (resolvedItem, item);
					}
					break;
				}
			}

			void AddExpressionWarning (ExpressionNode n)
				=> Document.Diagnostics.Add (CoreDiagnostics.UnexpectedExpression,
				new TextSpan (n.Offset, n.Length),
				DescriptionFormatter.GetKindNoun (valueSymbol),
				valueSymbol.Name);

			// errors expected to be fixed by ChangeMisspelledNameFixProvider
			// captures the information needed by the fixer
			void AddMisspelledNameError (MSBuildDiagnosticDescriptor d, string symbolName, TextSpan symbolSpan, params object[] args)
			{
				Document.Diagnostics.Add (
					d,
					symbolSpan,
					ImmutableDictionary<string, object>.Empty
						.Add (DiagnosticProperty.MisspelledNameOrValue, symbolName),
					args
				);
			}
		}

		//note: the value is unescaped, so offsets within it are not valid
		void VisitPureLiteral (MSBuildElementSyntax elementSymbol, MSBuildAttributeSyntax attributeSymbol, ITypedSymbol valueSymbol, ExpressionText expressionText)
		{
			string value = expressionText.GetUnescapedValue (true, out var trimmedOffset, out var escapedLength);

			MSBuildValueKind kind = valueSymbol.ValueKindWithoutModifiers ();

			// we must only check CustomType property when kind is MSBuildValueKind.CustomType
			// as MSBuildValueKind.NuGetID kind hackily stashes unrelated info in CustomType property
			bool isCustomType = kind == MSBuildValueKind.CustomType;
			CustomTypeInfo? customType = isCustomType? valueSymbol.CustomType : null;

			if (kind == MSBuildValueKind.Bool) {
				// bool has special validation later
			}
			else if (!isCustomType || (customType is not null && !customType.AllowUnknownValues)) {
				bool isKnownValue = Document.GetSchemas (true).TryGetKnownValue (valueSymbol, value, out ITypedSymbol? knownValue, out bool isError);
				if (isError) {
					AddMisspelledValueError (CoreDiagnostics.UnknownValue, DescriptionFormatter.GetTitleCaseKindNoun (valueSymbol), valueSymbol.Name, value);
					return;
				}
				if (isKnownValue && knownValue is IVersionableSymbol versionableSymbol) {
					CheckDeprecated (versionableSymbol, expressionText);
				}
			}

			MSBuildValueKind kindOrBaseKind = customType?.BaseKind ?? kind;

			switch (kindOrBaseKind) {
			case MSBuildValueKind.Guid:
				if (!Guid.TryParse (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidGuid, value);
				}
				if (isCustomType && customType.AnalyzerHints.TryGetValue ("GuidFormat", out object guidFormat)) {
					try {
						if (!Guid.TryParseExact (value, (string)guidFormat, out _)) {
							AddErrorWithArgs (CoreDiagnostics.GuidIncorrectFormat, value, guidFormat);
						}
					} catch (FormatException ex) {
						Logger.LogError (ex, "`GuidFormat` analyzer hint has invalid value '{0}'", guidFormat);
					}
				}
				break;
			case MSBuildValueKind.Int:
				if (!long.TryParse (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidInteger, value);
				}
				break;
			case MSBuildValueKind.Bool:
				if (!bool.TryParse (value, out _)) {
					AddMisspelledValueError (CoreDiagnostics.InvalidBool, value);
				}
				break;
			case MSBuildValueKind.Url:
				if (!Uri.TryCreate (value, UriKind.Absolute, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidUrl, value);
				}
				break;
			case MSBuildValueKind.Version:
				if (!Version.TryParse (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidVersion, value);
				}
				break;
			case MSBuildValueKind.NuGetVersion:
				if (!NuGet.Versioning.VersionRange.TryParse (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidNuGetVersionExpression, value);
				}
				break;
			case MSBuildValueKind.VersionSuffixed:
				// TODO
				if (!NuGet.Versioning.NuGetVersion.TryParse (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidVersionSuffixed, value);
				}
				break;
			case MSBuildValueKind.TargetName:
				if (GetSchemasExcludingCurrentDocInferred ().GetTarget (value) is TargetInfo resolvedTarget) {
					CheckDeprecated (resolvedTarget, expressionText);
				} else {
					// this won't work as-is, as inference will add this instance of the item to the inferred schema
					// AddErrorWithArgs (CoreDiagnostics.UndefinedTarget, value);
				}
				break;
			case MSBuildValueKind.PropertyName:
				if (GetSchemasExcludingCurrentDocInferred ().GetProperty (value, true) is PropertyInfo resolvedProperty) {
					CheckDeprecated (resolvedProperty, expressionText);
					if (attributeSymbol?.SyntaxKind == MSBuildSyntaxKind.Output_PropertyName) {
						CheckPropertyWrite (resolvedProperty, expressionText.Span);
					}
				} else {
					// FIXME: this won't work as-is, as inference will add this instance of the item to the inferred schema
					//AddErrorWithArgs (CoreDiagnostics.UnknownProperty, value);
				}
				break;
			case MSBuildValueKind.ItemName:
				if (GetSchemasExcludingCurrentDocInferred ().GetItem (value) is ItemInfo resolvedItem) {
					CheckDeprecated (resolvedItem, expressionText);
				} else {
					// FIXME: this won't work as-is, as inference will add this instance of the item to the inferred schema
					// AddErrorWithArgs (CoreDiagnostics.UnknownProperty, value);
				}
				break;
			case MSBuildValueKind.Lcid:
				if (!CultureHelper.IsValidLcid (value, out int lcid)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidLcid, value);
				} else if (!CultureHelper.IsKnownLcid (lcid)) {
					AddErrorWithArgs (CoreDiagnostics.UnknownLcid, value);
				}
				break;
			case MSBuildValueKind.Culture:
				if (!CultureHelper.IsValidCultureName (value)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidCulture, value);
				} else if (!CultureHelper.IsKnownCulture (value)) {
					AddErrorWithArgs (CoreDiagnostics.UnknownCulture, value);
				}
				break;
			case MSBuildValueKind.TargetFramework:
				switch (FrameworkInfoProvider.Instance.ValidateFrameworkShortName (value, out var frameworkComponent, out var versionComponent, out var platformComponent, out var profileComponent, out var platformVersionComponent)) {
				case FrameworkNameValidationResult.OK:
					break;
				case FrameworkNameValidationResult.Malformed:
					AddErrorWithArgs (CoreDiagnostics.InvalidTargetFramework, value);
					break;
				case FrameworkNameValidationResult.UnknownIdentifier:
					AddErrorWithArgs (CoreDiagnostics.UnknownTargetFramework, value);
					break;
				case FrameworkNameValidationResult.UnknownVersion:
					AddErrorWithArgs (CoreDiagnostics.TargetFrameworkHasUnknownVersion, value, versionComponent);
					break;
				case FrameworkNameValidationResult.UnknownPlatform:
					AddErrorWithArgs (CoreDiagnostics.TargetFrameworkHasUnknownTargetPlatform, value, platformComponent);
					break;
				case FrameworkNameValidationResult.UnknownProfile:
					AddErrorWithArgs (CoreDiagnostics.TargetFrameworkHasUnknownProfile, value, profileComponent);
					break;
				case FrameworkNameValidationResult.UnknownPlatformVersion:
					AddErrorWithArgs (CoreDiagnostics.TargetFrameworkHasUnknownTargetPlatformVersion, value, platformVersionComponent, platformComponent);
					break;
				}
				break;
			case MSBuildValueKind.TargetFrameworkIdentifier:
				if (!FrameworkInfoProvider.Instance.IsKnownFrameworkIdentifier (value)) {
					AddErrorWithArgs (CoreDiagnostics.UnknownTargetFrameworkIdentifier, value);
				}
				break;
			case MSBuildValueKind.TargetFrameworkVersion: {
					if (!Version.TryParse (value.TrimStart ('v', 'V'), out Version fxv)) {
						AddErrorWithArgs (CoreDiagnostics.InvalidVersion, value);
						break;
					}
					fxv = new Version (Math.Max (fxv.Major, 0), Math.Max (fxv.Minor, 0), Math.Max (fxv.Revision, 0), Math.Max (fxv.Build, 0));
					if (Document is MSBuildRootDocument d && d.Frameworks.Count > 0) {
						bool foundMatch = false;
						foreach (var fx in d.Frameworks) {
							if (FrameworkInfoProvider.AreVersionsEquivalent (fx.Version, fxv) && FrameworkInfoProvider.Instance.IsKnownFrameworkVersion (fx.Framework, fxv)) {
								foundMatch = true;
							}
						}
						if (!foundMatch) {
							AddErrorWithArgs (CoreDiagnostics.UnknownTargetFrameworkVersion, value, d.Frameworks[0].Framework);
						}
					}
					break;
				}
			case MSBuildValueKind.TargetFrameworkProfile: {
					if (Document is MSBuildRootDocument d && d.Frameworks.Count > 0) {
						bool foundMatch = false;
						foreach (var fx in d.Frameworks) {
							if (fx.Profile == value && FrameworkInfoProvider.Instance.IsFrameworkProfileValid (fx.Framework, fx.Version, value)) {
								foundMatch = true;
							}
						}
						if (!foundMatch) {
							AddErrorWithArgs (CoreDiagnostics.UnknownTargetFrameworkProfile, value, d.Frameworks[0].Framework, d.Frameworks[0].Version);
						}
					}
					break;
				}
			case MSBuildValueKind.ClrNamespace:
				if (!IsValidTypeOrNamespace (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidClrNamespace, value);
				}
				break;

			case MSBuildValueKind.ClrType:
				if (!IsValidTypeOrNamespace (value, out _)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidClrType, value);
				}
				break;

			case MSBuildValueKind.ClrTypeName:
				if (!(IsValidTypeOrNamespace (value, out int componentCount) && componentCount == 1)) {
					AddErrorWithArgs (CoreDiagnostics.InvalidClrTypeName, value);
				}
				break;
			}

			void AddErrorWithArgs (MSBuildDiagnosticDescriptor d, params object[] args) => Document.Diagnostics.Add (d, new TextSpan (trimmedOffset, escapedLength), args);

			// misspelled value expected to be fixed by ChangeMisspelledNameFixProvider
			void AddMisspelledValueError (MSBuildDiagnosticDescriptor d, params object[] args)
			{
				Document.Diagnostics.Add (
					d,
					new TextSpan (trimmedOffset, escapedLength),
					ImmutableDictionary<string, object>.Empty
						.Add (DiagnosticProperty.MisspelledNameOrValue, value)
						.Add (DiagnosticProperty.MisspelledValueExpectedType, valueSymbol),
					args
				);
			}

			static bool IsValidTypeOrNamespace (string value, out int componentCount)
			{
				string[] components = value.Split ('.');
				componentCount = components.Length;
				foreach (var component in components) {
					if (!System.CodeDom.Compiler.CodeGenerator.IsValidLanguageIndependentIdentifier (component)) {
						return false;
					}
				}
				return true;
			}
		}

		void CheckPropertyWrite (PropertyInfo resolvedProperty, TextSpan span)
		{
			if (resolvedProperty.IsReserved) {
				Document.Diagnostics.Add (CoreDiagnostics.PropertyWriteReserved, span, resolvedProperty.Name);
			} else if (resolvedProperty.IsReadOnly) {
				Document.Diagnostics.Add (CoreDiagnostics.PropertyWriteReadonly, span, resolvedProperty.Name);
			}
		}

		bool IsItemUsed (string itemName, ReferenceUsage usage, out ItemInfo resolvedItem)
		{
			// if it's been found in an imported file or an explicit schema, it counts as used
			resolvedItem = GetSchemasExcludingCurrentDocInferred ().GetItem (itemName);
			if (resolvedItem is not null) {
				return true;
			}

			// if it's used in some other way in the current file, it's valid
			if (Document.InferredSchema.ItemUsage.TryGetValue (itemName, out var u)) {
				if ((u & usage) != 0) {
					return true;
				}
			}
			return false;
		}

		bool IsPropertyUsed (string propertyName, ReferenceUsage usage, out PropertyInfo resolvedProperty)
		{
			// if it's been found in an imported file or an explicit schema, it counts as used
			resolvedProperty = GetSchemasExcludingCurrentDocInferred ().GetProperty (propertyName, true);
			if (resolvedProperty is not null) {
				return true;
			}

			// if it's used in some other way in the current file, it's valid
			if (Document.InferredSchema.PropertyUsage.TryGetValue (propertyName, out var u)) {
				if ((u & usage) != 0) {
					return true;
				}
			}
			return false;
		}

		bool IsMetadataUsed (string itemName, string metadataName, ReferenceUsage usage, out MetadataInfo resolvedMetadata)
		{
			// if it's been found in an imported file or an explicit schema, it's valid
			resolvedMetadata = GetSchemasExcludingCurrentDocInferred ().GetMetadata (itemName, metadataName, true);
			if (resolvedMetadata is not null) {
				return true;
			}

			// if it's used in some other way in the current file, it's valid
			if (Document.InferredSchema.MetadataUsage.TryGetValue ((itemName, metadataName), out var u)) {
				if ((u & usage) != 0) {
					return true;
				}
			}
			return false;
		}
	}
}