﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using Sitefinity_CLI.Exceptions;
using Sitefinity_CLI.Model;
using Sitefinity_CLI.PackageManagement.Contracts;
using Sitefinity_CLI.VisualStudio;

namespace Sitefinity_CLI.PackageManagement.Implementations
{
    internal class SitefinityPackageManager : ISitefinityPackageManager
    {
        public SitefinityPackageManager(
            INuGetApiClient nuGetApiClient,
            INuGetCliClient nuGetCliClient,
            IPackagesConfigFileEditor packagesConfigFileEditor,
            IProjectConfigFileEditor projectConfigFileEditor,
            ILogger<SitefinityPackageManager> logger)
        {
            this.nuGetApiClient = nuGetApiClient;
            this.nuGetCliClient = nuGetCliClient;
            this.packagesConfigFileEditor = packagesConfigFileEditor;
            this.projectConfigFileEditor = projectConfigFileEditor;
            this.logger = logger;
            this.defaultSources = new List<PackageSource>()
            {
               new PackageSource(SitefinityPublicNuGetSource),
               new PackageSource(PublicNuGetSourceV3)
            };
            this.supportedFrameworksRegex = new Regex("^net[0-9]*$", RegexOptions.Compiled);
            this.systemAssembliesNotToUpdate = new HashSet<string>() { "System.Runtime", "System.IO" };
        }

        public void Install(string packageId, string version, string solutionFilePath, string nugetConfigFilePath)
        {
            string solutionDirectory = Path.GetDirectoryName(solutionFilePath);

            this.logger.LogInformation($"[{solutionDirectory}] Installing package '{packageId}'...");


            this.nuGetCliClient.InstallPackage(packageId, version, solutionDirectory, nugetConfigFilePath);

            this.logger.LogInformation($"[{solutionDirectory}] Install for package '{packageId}' completed");
        }

        public void Restore(string solutionFilePath)
        {
            this.logger.LogInformation($"[{solutionFilePath}] Restoring packages started...");

            this.nuGetCliClient.Restore(solutionFilePath);

            this.logger.LogInformation($"[{solutionFilePath}] Restoring packages completed");
        }

        public bool PackageExists(string packageId, string projectFilePath)
        {
            string packagesConfigFilePath = GetPackagesConfigFilePathForProject(projectFilePath);

            NuGetPackage nuGetPackage = this.packagesConfigFileEditor.FindPackage(packagesConfigFilePath, packageId);
            if (nuGetPackage != null)
            {
                return true;
            }

            return false;
        }

        public async Task<NuGetPackage> GetSitefinityPackageTree(string version)
        {
            return await this.GetSitefinityPackageTree(version, this.defaultSources);
        }

        public async Task<NuGetPackage> GetSitefinityPackageTree(string version, IEnumerable<PackageSource> nugetPackageSources)
        {
            var sourcesUsed = string.Join(',', nugetPackageSources?.Select(x => x.Source));
            this.logger.LogInformation($"Package sources used: {sourcesUsed}");

            return await this.nuGetApiClient.GetPackageWithFullDependencyTree(Constants.SitefinityAllNuGetPackageId, version, nugetPackageSources, this.supportedFrameworksRegex);
        }

        public async Task<NuGetPackage> GetPackageTree(string id, string version, IEnumerable<PackageSource> nugetPackageSources, Func<NuGetPackage, bool> breakPackageCalculationPrediacte = null)
        {
            return await this.nuGetApiClient.GetPackageWithFullDependencyTree(id, version, nugetPackageSources, supportedFrameworksRegex, breakPackageCalculationPrediacte);
        }

        public void SyncReferencesWithPackages(string projectFilePath, string solutionDir)
        {
            this.logger.LogInformation($"Synchronizing packages and references for project '{projectFilePath}'");

            string packagesConfigFilePath = GetPackagesConfigFilePathForProject(projectFilePath);
            IEnumerable<NuGetPackage> packages = this.packagesConfigFileEditor.GetPackages(packagesConfigFilePath);

            XmlDocument projectFileXmlDocument = new XmlDocument();
            projectFileXmlDocument.Load(projectFilePath);

            var processedAssemblies = new HashSet<string>();
            var projectDir = Path.GetDirectoryName(projectFilePath);

            var projectConfigPath = this.projectConfigFileEditor.GetProjectConfigPath(projectDir);
            XmlNodeList bindingRedirectNodes = null;
            XmlDocument projectConfig = null;
            if (!string.IsNullOrEmpty(projectConfigPath))
            {
                projectConfig = new XmlDocument();
                projectConfig.Load(projectConfigPath);
                bindingRedirectNodes = projectConfig.GetElementsByTagName("dependentAssembly");
            }

            XmlNodeList referenceElements = projectFileXmlDocument.GetElementsByTagName(Constants.ReferenceElem);
            string targetFramework = GetTargetFramework(projectFileXmlDocument);

            IEnumerable<AssemblyReference> nugetPackageAssemblyReferences = this.GetAssemblyReferencesFromNuGetPackages(packages, targetFramework, projectDir, solutionDir);
            IEnumerable<IGrouping<string, AssemblyReference>> nuGetPackageAssemblyReferenceGroups = nugetPackageAssemblyReferences
                .Where(ar => ar.Version != null && !this.systemAssembliesNotToUpdate.Contains(ar.Name))
                .GroupBy(ar => ar.Name);

            // Foreach package installed for this project, check if all DLLs are included. If not - include missing ones. Fix binding redirects in web.config if necessary.
            foreach (IGrouping<string, AssemblyReference> nuGetPackageAssemblyReferenceGroup in nuGetPackageAssemblyReferenceGroups)
            {
                this.AddOrUpdateReferencesForAssembly(projectFileXmlDocument, referenceElements, bindingRedirectNodes, projectConfig, nuGetPackageAssemblyReferenceGroup.Key, nuGetPackageAssemblyReferenceGroup);
            }

            IEnumerable<string> nugetPackageRelativeFileReferences = this.GetRelativeFilePathsFromNuGetPackages(packages, projectDir, solutionDir);
            this.RemoveReferencesToMissingNuGetPackageDlls(projectDir, solutionDir, projectFileXmlDocument, nugetPackageRelativeFileReferences);

            projectFileXmlDocument.Save(projectFilePath);
            projectConfig?.Save(projectConfigPath);

            this.logger.LogInformation($"Synchronization completed for project '{projectFilePath}'");
        }

        private void RemoveReferencesToMissingNuGetPackageDlls(string projectDir, string solutionDir, XmlDocument projectFileXmlDocument, IEnumerable<string> nugetPackageRelativeFileReferences)
        {
            string packagesDir = Path.Combine(solutionDir, PackagesFolderName);
            string relativePackagesDirPath = GetRelativePathTo(projectDir + "\\", packagesDir);

            XmlNodeList elementsWithIncludeAttribute = projectFileXmlDocument.SelectNodes("//*[@Include]");
            for (int i = 0; i < elementsWithIncludeAttribute.Count; i++)
            {
                XmlNode elementWithIncludeAttribute = elementsWithIncludeAttribute[i];
                XmlAttribute includeAttr = elementWithIncludeAttribute.Attributes[Constants.IncludeAttribute];
                string includeAttributeValue = includeAttr.Value;

                if (includeAttributeValue.StartsWith(relativePackagesDirPath, StringComparison.OrdinalIgnoreCase) &&
                    !nugetPackageRelativeFileReferences.Any(fr => fr.Equals(includeAttributeValue, StringComparison.OrdinalIgnoreCase)))
                {
                    this.logger.LogInformation($"Removing '{elementWithIncludeAttribute.Name}' element with include attribute '{includeAttributeValue}', because file cannot be found in NuGet packages installed for this project.");
                    elementWithIncludeAttribute.ParentNode.RemoveChild(elementWithIncludeAttribute);
                }
            }
        }

        public IEnumerable<PackageSource> GetNugetPackageSources(string nugetConfigFilePath)
        {
            if (string.IsNullOrEmpty(nugetConfigFilePath))
            {
                throw new ArgumentException(nameof(nugetConfigFilePath));
            }

            var setting = Settings.LoadSpecificSettings(null, nugetConfigFilePath);
            var nugetPackageSources = PackageSourceProvider.LoadPackageSources(setting);

            // To be able to use .ProtoclVersion property of the library there must be xml attribute in the xml config file indicating the protocl version e.g. 2,3.
            // However most of the cients configs do not have that
            foreach (var source in nugetPackageSources)
            {
                source.ProtocolVersion = source.SourceUri.Segments.Contains(Constants.ApiV3IdentifierSegment) 
                    ? Constants.NugetProtoclV3 : Constants.NugetProtoclV2;
            }

            return nugetPackageSources;
        }

        private void AddOrUpdateReferencesForAssembly(XmlDocument projectFileXmlDocument, XmlNodeList referenceElements, XmlNodeList bindingRedirectNodes, XmlDocument projectConfig, string assemblyName, IEnumerable<AssemblyReference> nugetPackageAssemblyReferences)
        {
            AssemblyReference nugetPackageAssemblyReferenceWithNewestVersion = nugetPackageAssemblyReferences.OrderByDescending(ar => ar.Version).First();

            bool isAssemblyReferenceFound = false;
            for (int i = 0; i < referenceElements.Count; i++)
            {
                XmlNode referenceElement = referenceElements[i];
                XmlAttribute includeAttribute = referenceElement.Attributes[Constants.IncludeAttribute];

                if (!string.IsNullOrWhiteSpace(includeAttribute.Value) &&
                    (includeAttribute.Value.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) || includeAttribute.Value.StartsWith(assemblyName + ",", StringComparison.OrdinalIgnoreCase)))
                {
                    Version currentAssemblyVersion = this.ExtractAssemblyVersionFromIncludeAttribute(includeAttribute.Value);

                    if (currentAssemblyVersion != null && currentAssemblyVersion > nugetPackageAssemblyReferenceWithNewestVersion.Version)
                    {
                        this.logger.LogInformation($"The assembly reference '{assemblyName}' is on version '{currentAssemblyVersion}'. It won't be downgraded to version '{nugetPackageAssemblyReferenceWithNewestVersion.Version}'.");
                        isAssemblyReferenceFound = true;
                        break;
                    }

                    string proccesorArchitecture = includeAttribute.Value.Split(',').FirstOrDefault(x => x.Contains(ProcessorArchitectureAttribute));
                    string includeAttributeNewValue = string.IsNullOrEmpty(proccesorArchitecture) ? nugetPackageAssemblyReferenceWithNewestVersion.FullName : $"{nugetPackageAssemblyReferenceWithNewestVersion.FullName},{proccesorArchitecture}";

                    if (!includeAttribute.Value.Equals(includeAttributeNewValue, StringComparison.OrdinalIgnoreCase))
                    {
                        this.logger.LogInformation($"Updated include attribue '{includeAttribute.Value}' to '{includeAttributeNewValue}'.");
                        includeAttribute.Value = includeAttributeNewValue;
                    }

                    XmlNode hintPathNode = this.GetChildNode(referenceElement, Constants.HintPathElem);
                    if (hintPathNode == null)
                    {
                        this.logger.LogInformation($"Added hint path '{nugetPackageAssemblyReferenceWithNewestVersion.HintPath}' for reference assembly '{nugetPackageAssemblyReferenceWithNewestVersion.FullName}'.");

                        hintPathNode = projectFileXmlDocument.CreateElement(Constants.HintPathElem, projectFileXmlDocument.DocumentElement.NamespaceURI);
                        referenceElement.AppendChild(hintPathNode);
                        hintPathNode.InnerText = nugetPackageAssemblyReferenceWithNewestVersion.HintPath;
                    }
                    else if (!nugetPackageAssemblyReferences.Any(ar => ar.Version == nugetPackageAssemblyReferenceWithNewestVersion.Version && ar.HintPath.Equals(hintPathNode.InnerText, StringComparison.OrdinalIgnoreCase)))
                    {
                        this.logger.LogInformation($"Updated hint path '{hintPathNode.InnerText}' to '{nugetPackageAssemblyReferenceWithNewestVersion.HintPath}' for reference assembly '{nugetPackageAssemblyReferenceWithNewestVersion.FullName}'.");

                        hintPathNode.InnerText = nugetPackageAssemblyReferenceWithNewestVersion.HintPath;
                    }

                    isAssemblyReferenceFound = true;
                    break;
                }
            }

            if (!isAssemblyReferenceFound)
            {
                this.logger.LogInformation($"Added missing assembly reference '{nugetPackageAssemblyReferenceWithNewestVersion.FullName}' with hint path '{nugetPackageAssemblyReferenceWithNewestVersion.HintPath}'.");

                XmlNode referencesGroup = projectFileXmlDocument.GetElementsByTagName(Constants.ItemGroupElem)[0];
                XmlElement referenceNode = projectFileXmlDocument.CreateElement(Constants.ReferenceElem, projectFileXmlDocument.DocumentElement.NamespaceURI);

                XmlAttribute includeAttr = projectFileXmlDocument.CreateAttribute(Constants.IncludeAttribute);
                includeAttr.Value = nugetPackageAssemblyReferenceWithNewestVersion.FullName;
                referenceNode.Attributes.Append(includeAttr);

                XmlElement hintPathNode = projectFileXmlDocument.CreateElement(Constants.HintPathElem, projectFileXmlDocument.DocumentElement.NamespaceURI);
                hintPathNode.InnerText = nugetPackageAssemblyReferenceWithNewestVersion.HintPath;

                referenceNode.AppendChild(hintPathNode);
                referencesGroup.AppendChild(referenceNode);
            }

            this.SyncBindingRedirects(projectConfig, bindingRedirectNodes, assemblyName, nugetPackageAssemblyReferenceWithNewestVersion.Version.ToString());
        }

        private XmlNode GetChildNode(XmlNode node, string childNodeName)
        {
            XmlNodeList childNodes = node.ChildNodes;
            for (int i = 0; i < childNodes.Count; i++)
            {
                var childNode = childNodes[i];
                if (childNode.Name == childNodeName)
                {
                    return childNode;
                }
            }

            return null;
        }

        private IEnumerable<string> GetRelativeFilePathsFromNuGetPackages(IEnumerable<NuGetPackage> nuGetPackages, string projectDir, string solutionDir)
        {
            List<string> filePaths = new List<string>();
            foreach (NuGetPackage nuGetPackage in nuGetPackages)
            {
                string packageDir = this.GetNuGetPackageDir(solutionDir, nuGetPackage);
                filePaths.AddRange(Directory.GetFiles(packageDir, "*.*", SearchOption.AllDirectories));
            }

            return filePaths.Select(fp => GetRelativePathTo(projectDir + "\\", fp));
        }

        private IEnumerable<AssemblyReference> GetAssemblyReferencesFromNuGetPackages(IEnumerable<NuGetPackage> nuGetPackages, string targetFramework, string projectDir, string solutionDir)
        {
            List<string> dllFilePaths = new List<string>();
            foreach (NuGetPackage nuGetPackage in nuGetPackages)
            {
                string packageDir = this.GetNuGetPackageDir(solutionDir, nuGetPackage);
                dllFilePaths.AddRange(this.GetPackageDlls(packageDir, targetFramework));
            }

            IEnumerable<AssemblyReference> assemblyReferences = dllFilePaths.Distinct().Select(d => this.GetAssemblyReferenceFromDllFilePath(d, projectDir));

            return assemblyReferences;
        }

        private string GetNuGetPackageDir(string solutionDir, NuGetPackage nuGetPackage)
        {
            string nuGetPackageFolderName = $"{nuGetPackage.Id}.{nuGetPackage.Version}";

            return Path.Combine(solutionDir, PackagesFolderName, nuGetPackageFolderName);
        }

        private AssemblyReference GetAssemblyReferenceFromDllFilePath(string dllFilePath, string projectDir)
        {
            Assembly assembly = Assembly.LoadFile(dllFilePath);
            AssemblyName assemblyName = assembly.GetName();

            AssemblyReference assemblyReference = new AssemblyReference();
            assemblyReference.Name = assemblyName.Name;
            assemblyReference.Version = assemblyName.Version;
            assemblyReference.FullName = assemblyName.FullName;
            assemblyReference.HintPath = GetRelativePathTo(projectDir + "\\", dllFilePath);

            return assemblyReference;
        }

        private Version ExtractAssemblyVersionFromIncludeAttribute(string includeAttributeValue)
        {
            string versionChunk = includeAttributeValue
                .Split(',')
                .FirstOrDefault(x => x.Contains("Version="));

            if (string.IsNullOrWhiteSpace(versionChunk))
            {
                this.logger.LogInformation($"Unable to extract the version from '{includeAttributeValue}'.");

                return null;
            }

            string assemblyVersionString = versionChunk
                .Split("=")
                .ToList()[1];

            Version parsedVersion = null;
            if (!Version.TryParse(assemblyVersionString, out parsedVersion))
            {
                this.logger.LogInformation($"Unable to parse version string '{assemblyVersionString}'.");
            }

            return parsedVersion;
        }

        public IEnumerable<PackageSource> DefaultPackageSource
        {
            get
            {
                return new List<PackageSource>(this.defaultSources);
            }
        }

        private void SyncBindingRedirects(XmlDocument configDoc, XmlNodeList bindingRedirectNodes, string assemblyFullName, string assemblyVersion)
        {
            if (bindingRedirectNodes != null)
            {
                foreach (XmlNode node in bindingRedirectNodes)
                {
                    XmlNode assemblyIdentity = null;
                    XmlNode bindingRedirect = null;
                    foreach (XmlNode childNode in node.ChildNodes)
                    {
                        if (childNode.Name == AssemblyIdentityAttributeName)
                            assemblyIdentity = childNode;

                        if (childNode.Name == BindingRedirectAttributeName)
                            bindingRedirect = childNode;
                    }

                    if (assemblyIdentity != null && bindingRedirect != null)
                    {
                        var name = assemblyIdentity.Attributes["name"]?.Value;
                        if (name == assemblyFullName)
                        {
                            var newVersionAttribute = bindingRedirect.Attributes[NewVersionAttributeName];
                            if (newVersionAttribute != null && !ShouldUpdateBindingRedirect(newVersionAttribute.Value, assemblyVersion))
                            {
                                break;
                            }

                            if (newVersionAttribute == null)
                            {
                                newVersionAttribute = configDoc.CreateAttribute(Constants.IncludeAttribute);

                                bindingRedirect.Attributes.Append(newVersionAttribute);
                            }

                            newVersionAttribute.Value = assemblyVersion;

                            var oldVersionAttribute = bindingRedirect.Attributes[OldVersionAttributeName];
                            if (oldVersionAttribute == null)
                            {
                                oldVersionAttribute = configDoc.CreateAttribute(Constants.IncludeAttribute);

                                bindingRedirect.Attributes.Append(oldVersionAttribute);
                            }

                            oldVersionAttribute.Value = $"0.0.0.0-{assemblyVersion}";

                            break;
                        }
                    }
                }
            }
        }

        private static bool ShouldUpdateBindingRedirect(string oldAssemblyVersion, string newAssemblyVersion)
        {
            var oldVersion = Version.Parse(oldAssemblyVersion);
            var newVersion = Version.Parse(newAssemblyVersion);

            return newVersion > oldVersion;
        }

        private static bool TrySetTargetFramework(XmlDocument doc, string targetFramework)
        {
            var targetFrameworkVersionElems = doc.GetElementsByTagName(Constants.TargetFrameworkVersionElem);
            if (targetFrameworkVersionElems.Count != 1)
            {
                throw new InvalidOperationException("Unable to set the target framework");
            }

            if (targetFrameworkVersionElems[0].InnerText != targetFramework)
            {
                targetFrameworkVersionElems[0].InnerText = targetFramework;

                return true;
            }

            return false;
        }

        private static string GetTargetFramework(XmlDocument doc)
        {
            XmlNodeList targetFrameworkVersionElems = doc.GetElementsByTagName(Constants.TargetFrameworkVersionElem);
            if (targetFrameworkVersionElems.Count != 1)
            {
                throw new InvalidOperationException("Unable to get the target framework");
            }

            return targetFrameworkVersionElems[0].InnerText;
        }

        private static string GetTargetFrameworkForSitefinityVersion(string version)
        {
            var versionWithoutSeperator = version.Replace(".", string.Empty).Substring(0, 3);
            var versionAsInt = int.Parse(versionWithoutSeperator);

            if (versionAsInt < 100)
            {
                return "v4.0";
            }
            else if (versionAsInt >= 100 && versionAsInt <= 102)
            {
                return "v4.5";
            }
            else if (versionAsInt >= 110 && versionAsInt <= 112)
            {
                return "v4.7.1";
            }
            else if (versionAsInt >= 120 && versionAsInt < 132)
            {
                return "v4.7.2";
            }
            else if (versionAsInt >= 132)
            {
                return "v4.8";
            }

            return string.Empty;
        }

        /// <summary>
        /// Gets the dlls that should be referenced for the current package
        /// Check https://docs.microsoft.com/en-us/nuget/create-packages/supporting-multiple-target-frameworks for more info
        /// </summary>
        internal IEnumerable<string> GetPackageDlls(string packagePath, string targetVersion)
        {
            // v4.7.2 -> 472
            string versionPart = targetVersion.Replace(".", string.Empty).Replace("v", string.Empty);
            if (!int.TryParse(versionPart, out int targetFrameworkVersion) || targetFrameworkVersion == 0)
            {
                return Array.Empty<string>();
            }

            string libDir = Path.Combine(packagePath, LibFolderName);
            string dllStorageDir = null;
            if (Directory.Exists(libDir))
            {
                if (Directory.GetDirectories(libDir).Any())
                {
                    // we check for the highest possible .net framework version of the dll
                    dllStorageDir = GetDllStoragePathForNetFramework(targetFrameworkVersion, libDir);

                    if (string.IsNullOrEmpty(dllStorageDir))
                    {
                        // if there is no .net framework dll we check for .netstandard
                        dllStorageDir = GetDllStoragePathForNetStandart(libDir);
                    }
                }
            }
            else
            {
                dllStorageDir = packagePath;
            }

            return dllStorageDir != null && Directory.Exists(dllStorageDir) ? Directory.GetFiles(dllStorageDir, DllFilterPattern) : Array.Empty<string>();
        }

        private static string GetDllStoragePathForNetStandart(string libDir)
        {
            string dllStorageDir = null;
            int highestNetStandardDirFrameworkVersion = 0;

            string[] netStandardDirNames = Directory.GetDirectories(libDir, DotNetStandardPrefix + "*");
            foreach (string netStandardDirName in netStandardDirNames)
            {
                // netstandard2.0 => 2.0 => 20
                string netStandardDirFrameworkVersionString = netStandardDirName
                    .Split("\\")
                    .Last()
                    .Replace(DotNetStandardPrefix, string.Empty)
                    .Replace(".", string.Empty);

                if (int.TryParse(netStandardDirFrameworkVersionString, out int netStandardDirFrameworkVersion) &&
                    netStandardDirFrameworkVersion != 0 &&
                    netStandardDirFrameworkVersion <= 20 &&
                    netStandardDirFrameworkVersion > highestNetStandardDirFrameworkVersion)
                {
                    dllStorageDir = netStandardDirName;
                    highestNetStandardDirFrameworkVersion = netStandardDirFrameworkVersion;
                }
            }

            return dllStorageDir;
        }

        private static string GetDllStoragePathForNetFramework(int targetFrameworkVersion, string libDir)
        {
            if (targetFrameworkVersion.ToString().Length == 2)
            {
                // Fix for the cases when we upgrade from versions with different length - 4.7.1 to 4.8
                targetFrameworkVersion *= 10;
            }

            string dllStorageDir = null;
            int highestFolderFrameworkVersion = 0;
            IEnumerable<string> netFrameworkDirNames = Directory.GetDirectories(libDir)
                .Where(dirName => dirName.Contains(DotNetPrefix) && !dirName.Contains(DotNetStandardPrefix));

            foreach (string netFrameworkDirName in netFrameworkDirNames)
            {
                var netFrameworkDirVersionString = netFrameworkDirName.Split("\\").Last().Replace(DotNetPrefix, string.Empty);

                // The folder may have "-" in the name
                if (netFrameworkDirVersionString.Contains('-'))
                {
                    netFrameworkDirVersionString = netFrameworkDirVersionString.Split("-").FirstOrDefault();
                }

                if (int.TryParse(netFrameworkDirVersionString, out int netFrameworkDirVersion))
                {
                    if (netFrameworkDirVersion.ToString().Length == 2)
                    {
                        // Fix for the cases when we upgrade from versions with different length - 4.7.1 to 4.8
                        netFrameworkDirVersion *= 10;
                    }

                    if (netFrameworkDirVersion != 0 &&
                        netFrameworkDirVersion <= targetFrameworkVersion &&
                        netFrameworkDirVersion > highestFolderFrameworkVersion)
                    {
                        dllStorageDir = netFrameworkDirName;
                        highestFolderFrameworkVersion = netFrameworkDirVersion;
                    }
                }
            }

            return dllStorageDir;
        }

        private static string GetPackagesConfigFilePathForProject(string projectFilePath)
        {
            string projectDirectory = Path.GetDirectoryName(projectFilePath);
            string packagesConfigFilePath = Path.Combine(projectDirectory, Constants.PackagesConfigFileName);

            if (!File.Exists(packagesConfigFilePath))
            {
                throw new FileNotFoundException($"File '{Constants.PackagesConfigFileName}' not found in project directory '{projectDirectory}'. Cannot proceed with the upgrade.");
            }

            return packagesConfigFilePath;
        }

        private static string GetRelativePathTo(string fromPath, string toPath)
        {
            var fromUri = new Uri(fromPath);
            var toUri = new Uri(toPath);

            var relativeUri = fromUri.MakeRelativeUri(toUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        public void SetTargetFramework(IEnumerable<string> sitefinityProjectFilePaths, string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                throw new ArgumentException($"Invalid version: {version}");
            }

            var targetFramework = GetTargetFrameworkForSitefinityVersion(version);

            foreach (var projectFilePath in sitefinityProjectFilePaths)
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(projectFilePath);

                if (TrySetTargetFramework(doc, targetFramework))
                {
                    doc.Save(projectFilePath);

                    this.logger.LogInformation(string.Format(Constants.TargetFrameworkChanged, Path.GetFileName(projectFilePath), targetFramework));
                }
                else
                {
                    this.logger.LogInformation(string.Format(Constants.TargetFrameworkDoesNotNeedChanged, Path.GetFileName(projectFilePath), targetFramework));
                }
            }
        }

        private readonly INuGetApiClient nuGetApiClient;

        private readonly INuGetCliClient nuGetCliClient;
        private readonly IPackagesConfigFileEditor packagesConfigFileEditor;

        private readonly IProjectConfigFileEditor projectConfigFileEditor;

        private readonly ILogger logger;

        private readonly IEnumerable<PackageSource> defaultSources;

        private readonly Regex supportedFrameworksRegex;

        private readonly HashSet<string> systemAssembliesNotToUpdate;

        private const string SitefinityPublicNuGetSource = "https://nuget.sitefinity.com/nuget/";

        private const string PublicNuGetSourceV3 = "https://api.nuget.org/v3/index.json";
        //private const string PublicNuGetSourceV3 = "https://www.nuget.org/api/v2";

        private const string PackagesFolderName = "packages";

        private const string LibFolderName = "lib";

        private const string DotNetPrefix = "net";

        private const string DllFilterPattern = "*.dll";

        private const string ProcessorArchitectureAttribute = "processorArchitecture";

        private const string AssemblyIdentityAttributeName = "assemblyIdentity";

        private const string BindingRedirectAttributeName = "bindingRedirect";

        private const string OldVersionAttributeName = "oldVersion";

        private const string NewVersionAttributeName = "newVersion";

        private const string DotNetStandardPrefix = "netstandard";
    }
}
