// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace MonoDevelop.MSBuild.Editor.VisualStudio
{
	class PackageConsts
	{
		public const string PackageGuid = "6C7BD60D-5321-4FB0-8684-9736003D64AD";
		public const string PackageVersion = ThisAssembly.AssemblyInformationalVersion;

		public const string LanguageServiceGuid = "111E2ECB-9E5F-4945-9D21-D4E5368D620B";

		public const string LanguageServiceName = "MSBuild";
		public const string LanguageServiceKey = @"Languages\Language Services\" + LanguageServiceName;
	}

	class PackageResxId
	{
		/// <summary>
		/// MSBuild Editor
		/// </summary>
		public const int EditorName = 110;
		public const string EditorNameStr = "#110";

		public const int PackageName = 110;
		public const string PackageNameStr = "#110";

		/// <summary>
		/// MSBuild
		/// </summary>
		public const int LanguageName = 111;
		public const string LanguageNameStr = "#111";

		/// <summary>
		/// Language service for MSBuild projects and targets
		/// </summary>
		public const int PackageDescription = 112;
		public const string PackageDescriptionStr = "#112";
	}
}