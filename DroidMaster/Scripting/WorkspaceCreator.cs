﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace DroidMaster.Scripting {
	///<summary>Loads device scripts into a Roslyn Workspace.</summary>
	abstract class WorkspaceCreator {
		///<summary>Namespaces referenced in every source file.</summary>
		static readonly IReadOnlyCollection<string> StandardNamespaces = new[] {
			"System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Text",
			"System.Threading.Tasks", "System.Xml.Linq",
			"DroidMaster", "DroidMaster.Models"
		};

		static IReadOnlyCollection<string> StandardReferences = new[] {
			"mscorlib", "System", "System.Core", "System.Xml.Linq",
			typeof(WorkspaceCreator).Assembly.GetName().Name
		};

		///<summary>Maps file extensions to Roslyn language names.</summary>
		static readonly Dictionary<string, string> LanguageExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			{ ".cs", LanguageNames.CSharp },
			{ ".vb", LanguageNames.VisualBasic },
		};

		///<summary>Maps Roslyn language names to the prefix and suffix to wrap reference files in.</summary>
		static readonly IReadOnlyDictionary<string, Tuple<string, string>> ReferenceWrappers = new Dictionary<string, Tuple<string, string>> {
			{ LanguageNames.CSharp, Tuple.Create(string.Concat(StandardNamespaces.Select(n => $"using {n};\r\n")), "") },
			{ LanguageNames.VisualBasic, Tuple.Create(string.Concat(StandardNamespaces.Select(n => $"Imports {n}\r\n")), "") }
		};

		///<summary>Maps Roslyn language names to the prefix and suffix to wrap scripts in.</summary>
		static readonly IReadOnlyDictionary<string, Tuple<string, string>> ScriptWrappers = new Dictionary<string, Tuple<string, string>> {
			{ LanguageNames.CSharp, Tuple.Create(
				string.Concat(StandardNamespaces.Select(n => $"using {n};\r\n"))
			  + "\r\npublic async Task Run(DeviceModel device) {\r\n",
				"\r\n}") },
			{ LanguageNames.VisualBasic, Tuple.Create(
				string.Concat(StandardNamespaces.Select(n => $"Imports {n}\r\n"))
			  + "\r\nPublic Async Function Run(device As DeviceModel) As Task\r\n",
				"\r\nEnd Function") }
		};


		///<summary>Gets the directory to load scripts from.</summary>
		public string ScriptDirectory { get; }

		///<summary>Gets the Workspace manipulated by this instance.</summary>
		public Workspace Workspace { get; }

		///<summary>Gets project references to add to every script.</summary>
		public IReadOnlyCollection<ProjectId> ReferenceProjects { get; private set; }

		protected WorkspaceCreator(Workspace workspace, string scriptDirectory) {
			Workspace = workspace;
			ScriptDirectory = scriptDirectory;
		}

		///<summary>Refreshes the list of projects referenced by every script, updating the references for all script projects.</summary>
		public void RefreshReferenceProjects() {
			foreach (var id in ReferenceProjects ?? new ProjectId[0])
				Workspace.TryApplyChanges(Workspace.CurrentSolution.RemoveProject(id));

			ReferenceProjects = LanguageExtensions.Select(kvp => {
				var projectName = "DroidMaster.References." + kvp.Value;
				var project = Workspace.CurrentSolution
					.AddProject(projectName, projectName, kvp.Value)
					.AddMetadataReferences(StandardReferences.Select(CreateAssemblyReference));

				project = project.WithParseOptions(project.ParseOptions.WithKind(SourceCodeKind.Script));
				Workspace.TryApplyChanges(project.Solution);

				foreach (var path in Directory.EnumerateFiles(ScriptDirectory, "*" + kvp.Key)
											  .Where(f => f.StartsWith("_"))) {
					OpenDocument(project.Id, path, ReferenceWrappers[kvp.Value]);
				}
				return project.Id;
			}).ToList();

			foreach (var scriptProject in Workspace.CurrentSolution.ProjectIds.Except(ReferenceProjects)) {
				Workspace.TryApplyChanges(Workspace.CurrentSolution
					.WithProjectReferences(scriptProject, ReferenceProjects.Select(p => new ProjectReference(p)))
				);
			}
		}

		///<summary>Creates a project for the specified script file.</summary>
		public Project CreateScriptProject(string scriptFile) {
			if (ReferenceProjects == null) RefreshReferenceProjects();

			var name = Path.GetFileNameWithoutExtension(scriptFile);
			var language = LanguageExtensions[Path.GetExtension(scriptFile)];

			var project = Workspace.CurrentSolution
				.AddProject(name, name, language)
				.AddMetadataReferences(StandardReferences.Select(CreateAssemblyReference))
				.AddProjectReferences(ReferenceProjects.Select(p => new ProjectReference(p)));

			project = project.WithParseOptions(project.ParseOptions.WithKind(SourceCodeKind.Script));
			Workspace.TryApplyChanges(project.Solution);

			OpenDocument(project.Id, scriptFile, ScriptWrappers[language]);
			return Workspace.CurrentSolution.GetProject(project.Id);
		}

		///<summary>Opens a file path into a Roslyn <see cref="Document"/>, and adds the document to a project in the current solution.</summary>
		/// <param name="projectId">The project to create the document in.</param>
		/// <param name="path">The path to the file to open.</param>
		/// <param name="wrapper">The strings to wrap the file contents in.</param>
		///<remarks>In editor scenarios, this should create a TextBuffer.</remarks>
		protected abstract void OpenDocument(ProjectId projectId, string path, Tuple<string, string> wrapper);

		///<summary>Creates a <see cref="MetadataReference"/> to the specified assembly.</summary>
		///<param name="assemblyName">The name of the assembly to reference.  This must either be part of the BCL or loaded into the current process.</param>
		protected abstract MetadataReference CreateAssemblyReference(string assemblyName);
	}
}
