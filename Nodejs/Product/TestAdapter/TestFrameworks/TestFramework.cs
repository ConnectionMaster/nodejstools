﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudioTools.Project;
using Newtonsoft.Json;
using System.Linq;

namespace Microsoft.NodejsTools.TestAdapter.TestFrameworks {
    class TestFramework {
        private readonly string _vsixScriptFolder;
        private readonly string _findTestsScriptFile;
        private readonly string _runTestsScriptFile;

        public TestFramework(string vsixScriptFolder) {
            _vsixScriptFolder = vsixScriptFolder;
            Name = Path.GetFileNameWithoutExtension(vsixScriptFolder);
            _findTestsScriptFile = Path.Combine(Path.GetDirectoryName(vsixScriptFolder), "find_tests.js");
            _runTestsScriptFile = Path.Combine(Path.GetDirectoryName(vsixScriptFolder), "run_tests.js");
        }

        public string Name { get; private set; }
        public List<NodejsTestInfo> FindTests(IEnumerable<string> testFiles,
            string nodeExe,
            IMessageLogger logger, 
            string workingDirectory) {

            string testInfo = string.Empty;
            string discoverResultFile = Path.GetTempFileName();
            try {

                string stdout = EvaluateJavaScript(nodeExe, string.Join(";", testFiles), discoverResultFile, logger, workingDirectory);
                if (!String.IsNullOrWhiteSpace(stdout)) {
                    IEnumerable<String> stdoutLines = stdout.Split(new string[] {Environment.NewLine},
                        StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith("NTVS_ERROR:")).Select(s => s.Trim().Remove(0,11));

                    if (stdoutLines != null && stdoutLines.Count() > 0) {
                        foreach (string s in stdoutLines) {
                            logger.SendMessage(TestMessageLevel.Error, s);
                        }
                        //There was an error during detection, return an empty set
                        return new List<NodejsTestInfo>();
                    }
                }

                for (int i = 0; i < 4; i++) {
                    try {
                        testInfo = File.ReadAllText(discoverResultFile);
                        break;
                    } catch (IOException) {
                        //We took an error processing the file.  Wait a few and try again
                        Thread.Sleep(500);
                    }
                }
            } finally {
                try {
                    File.Delete(discoverResultFile);
                } catch (Exception) { //
                    //Unable to delete for some reason
                    //We leave the file behind in this case, its in TEMP so eventually OS will clean up
                }
            }

            List<NodejsTestInfo> testCases = new List<NodejsTestInfo>();
            List<DiscoveredTest> discoveredTests = (List<DiscoveredTest>)JsonConvert.DeserializeObject(testInfo, typeof(List<DiscoveredTest>));
            if (discoveredTests != null) {
                foreach (DiscoveredTest discoveredTest in discoveredTests) {
                    NodejsTestInfo test = new NodejsTestInfo(discoveredTest.File, discoveredTest.Test, discoveredTest.Suite, Name);
                    testCases.Add(test);
                }
            }
            return testCases;
        }

        public string[] ArgumentsToRunTests(string testName, string testFile, string workingDirectory, string projectRootDir) {
            workingDirectory = workingDirectory.TrimEnd(new char['\\']);
            projectRootDir = projectRootDir.TrimEnd(new char['\\']);
            return new string[] {
                WrapWithQuot(_runTestsScriptFile),
                Name,
                WrapWithQuot(testName),
                WrapWithQuot(testFile),
                WrapWithQuot(workingDirectory),
                WrapWithQuot(projectRootDir)
            };
        }

        private string WrapWithQuot(string path) {
            if (!path.StartsWith("\"") && !path.StartsWith("\'")) {
                path = "\"" + path + "\"";
            }
            return path;
        }

        private string EvaluateJavaScript(string nodeExePath, string testFile, string discoverResultFile, IMessageLogger logger, string workingDirectory) {
            workingDirectory = workingDirectory.TrimEnd(new char['\\']);
            string arguments = WrapWithQuot(_findTestsScriptFile)
                + " " + Name +
                " " + WrapWithQuot(testFile) +
                " " + WrapWithQuot(discoverResultFile) +
                " " + WrapWithQuot(workingDirectory);

            var processStartInfo = new ProcessStartInfo(nodeExePath, arguments);
            processStartInfo.CreateNoWindow = true;
            processStartInfo.UseShellExecute = false;
            processStartInfo.RedirectStandardError = true;
            processStartInfo.RedirectStandardOutput = true;

            string stdout = "";
            try {
                using (var process = Process.Start(processStartInfo)) {

                    process.EnableRaisingEvents = true;
                    process.OutputDataReceived += (sender, args) => {
                        stdout += args.Data + Environment.NewLine;

                    };
                    process.ErrorDataReceived += (sender, args) => {
                        stdout += args.Data + Environment.NewLine;

                    };
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    process.WaitForExit();
#if DEBUG
                    logger.SendMessage(TestMessageLevel.Informational, String.Format("  Process exited: {0}", process.ExitCode));
#endif
                }
#if DEBUG
                logger.SendMessage(TestMessageLevel.Informational, String.Format("  StdOut:{0}", stdout));
#endif
            } catch (FileNotFoundException e) {
                logger.SendMessage(TestMessageLevel.Error, String.Format("Error starting node.exe.\n {0}", e));
            }

            return stdout;
        }

        private class DiscoveredTest {
            public string Test { get; set; }
            public string Suite { get; set; }
            public string File { get; set; }
        }
    }
}
