﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.NodejsTools.TestAdapter.TestFrameworks;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;
using Newtonsoft.Json;
using MSBuild = Microsoft.Build.Evaluation;

namespace Microsoft.NodejsTools.TestAdapter
{
    [ExtensionUri(NodejsConstants.ExecutorUriString)]
    public class JavaScriptTestExecutor : ITestExecutor
    {
        private static readonly Version Node8Version = new Version(8, 0);

        //get from NodeRemoteDebugPortSupplier::PortSupplierId
        private static readonly Guid NodejsRemoteDebugPortSupplierUnsecuredId = new Guid("{9E16F805-5EFC-4CE5-8B67-9AE9B643EF80}");

        private readonly ManualResetEvent cancelRequested = new ManualResetEvent(false);

        private readonly ManualResetEvent testsCompleted = new ManualResetEvent(false);

        private ProcessOutput nodeProcess;
        private readonly object syncObject = new object();
        private List<TestCase> currentTests;
        private IFrameworkHandle frameworkHandle;
        private TestResult currentResult = null;
        private ResultObject currentResultObject = null;

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            this.EnsureInitialized();
            this.RunTestsCore(tests, runContext, frameworkHandle);
        }

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            this.EnsureInitialized();
            this.RunTestsCore(sources, runContext, frameworkHandle);
        }

        public void Cancel()
        {
            //let us just kill the node process there, rather do it late, because VS engine process 
            //could exit right after this call and our node process will be left running.
            KillNodeProcess();
            this.cancelRequested.Set();
        }

        private void EnsureInitialized()
        {
            if (JavaScriptTestDiscoverer.AssemblyResolver == null)
            {
                JavaScriptTestDiscoverer.AssemblyResolver = new AssemblyResolver();
            }
        }

        private void ProcessTestRunnerEmit(string line)
        {
            try
            {
                var testEvent = JsonConvert.DeserializeObject<TestEvent>(line);
                // Extract test from list of tests
                var tests = this.currentTests.Where(n => n.DisplayName == testEvent.title);
                if (tests.Count() > 0)
                {
                    switch (testEvent.type)
                    {
                        case "test start":
                            {
                                this.currentResult = new TestResult(tests.First())
                                {
                                    StartTime = DateTimeOffset.Now
                                };
                                this.frameworkHandle.RecordStart(tests.First());
                            }
                            break;
                        case "result":
                            {
                                RecordEnd(this.frameworkHandle, tests.First(), this.currentResult, testEvent.result);
                            }
                            break;
                        case "pending":
                            {
                                this.currentResult = new TestResult(tests.First());
                                RecordEnd(this.frameworkHandle, tests.First(), this.currentResult, testEvent.result);
                            }
                            break;
                    }
                }
                else if (testEvent.type == "suite end")
                {
                    this.currentResultObject = testEvent.result;
                    this.testsCompleted.Set();
                }
            }
            catch (JsonReaderException)
            {
                // Often lines emitted while running tests are not test results, and thus will fail to parse above
            }
        }

        /// <summary>
        /// This is the equivalent of "RunAll" functionality
        /// </summary>
        /// <param name="sources">Refers to the list of test sources passed to the test adapter from the client.  (Client could be VS or command line)</param>
        /// <param name="runContext">Defines the settings related to the current run</param>
        /// <param name="frameworkHandle">Handle to framework.  Used for recording results</param>
        private void RunTestsCore(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            ValidateArg.NotNull(sources, "sources");
            ValidateArg.NotNull(runContext, "runContext");
            ValidateArg.NotNull(frameworkHandle, "frameworkHandle");

            this.cancelRequested.Reset();

            var receiver = new TestReceiver();
            var discoverer = new JavaScriptTestDiscoverer();
            discoverer.DiscoverTests(sources, null, frameworkHandle, receiver);

            if (this.cancelRequested.WaitOne(0))
            {
                return;
            }

            this.RunTestsCore(receiver.Tests, runContext, frameworkHandle);
        }

        /// <summary>
        /// This is the equivalent of "Run Selected Tests" functionality.
        /// </summary>
        /// <param name="tests">The list of TestCases selected to run</param>
        /// <param name="runContext">Defines the settings related to the current run</param>
        /// <param name="frameworkHandle">Handle to framework.  Used for recording results</param>
        private void RunTestsCore(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            ValidateArg.NotNull(tests, "tests");
            ValidateArg.NotNull(runContext, "runContext");
            ValidateArg.NotNull(frameworkHandle, "frameworkHandle");
            this.cancelRequested.Reset();

            // .ts file path -> project settings
            var fileToTests = new Dictionary<string, List<TestCase>>();
            var sourceToSettings = new Dictionary<string, NodejsProjectSettings>();
            NodejsProjectSettings projectSettings = null;

            // put tests into dictionary where key is their source file
            foreach (var test in tests)
            {
                if (!fileToTests.ContainsKey(test.CodeFilePath))
                {
                    fileToTests[test.CodeFilePath] = new List<TestCase>();
                }
                fileToTests[test.CodeFilePath].Add(test);
            }

            // where key is the file and value is a list of tests
            foreach (var entry in fileToTests)
            {
                var firstTest = entry.Value.ElementAt(0);
                if (!sourceToSettings.TryGetValue(firstTest.Source, out projectSettings))
                {
                    sourceToSettings[firstTest.Source] = projectSettings = LoadProjectSettings(firstTest.Source);
                }

                this.currentTests = entry.Value;
                this.frameworkHandle = frameworkHandle;

                // Run all test cases in a given file
                RunTestCases(entry.Value, runContext, frameworkHandle, projectSettings);
            }
        }

        private bool HasVisualStudioProcessId(out int processId)
        {
            processId = 0;
            var pid = Environment.GetEnvironmentVariable(NodejsConstants.NodeToolsProcessIdEnvironmentVariable);
            if (pid == null)
            {
                return false;
            }

            return int.TryParse(pid, out processId);
        }

        private void RunTestCases(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle, NodejsProjectSettings settings)
        {
            // May be null, but this is handled by RunTestCase if it matters.
            // No VS instance just means no debugging, but everything else is
            // okay.
            if (tests.Count() == 0)
            {
                return;
            }

            var startedFromVs = this.HasVisualStudioProcessId(out var vsProcessId);

            var nodeArgs = new List<string>();
            // .njsproj file path -> project settings
            var sourceToSettings = new Dictionary<string, NodejsProjectSettings>();
            var testObjects = new List<TestCaseObject>();

            if (!File.Exists(settings.NodeExePath))
            {
                frameworkHandle.SendMessage(TestMessageLevel.Error, "Interpreter path does not exist: " + settings.NodeExePath);
                return;
            }

            // All tests being run are for the same test file, so just use the first test listed to get the working dir
            var firstTest = tests.First();
            var testFramework = firstTest.GetPropertyValue<string>(JavaScriptTestCaseProperties.TestFramework, defaultValue: "unknown");
            var testInfo = new NodejsTestInfo(firstTest.FullyQualifiedName, firstTest.CodeFilePath);
            var workingDir = Path.GetDirectoryName(CommonUtils.GetAbsoluteFilePath(settings.WorkingDir, testInfo.ModulePath));

            var nodeVersion = Nodejs.GetNodeVersion(settings.NodeExePath);

            // We can only log telemetry when we're running in VS.
            // Since the required assemblies are not on disk if we're not running in VS, we have to reference them in a separate method
            // this way the .NET framework only tries to load the assemblies when we actually need them.
            if (startedFromVs)
            {
                this.LogTelemetry(tests.Count(), nodeVersion, runContext.IsBeingDebugged, testFramework);
            }

            foreach (var test in tests)
            {
                if (this.cancelRequested.WaitOne(0))
                {
                    break;
                }

                if (settings == null)
                {
                    frameworkHandle.SendMessage(
                        TestMessageLevel.Error,
                        $"Unable to determine interpreter to use for '{test.Source}'.");
                    frameworkHandle.RecordEnd(test, TestOutcome.Failed);
                }

                var args = new List<string>();
                args.AddRange(GetInterpreterArgs(test, workingDir, settings.ProjectRootDir));

                // Fetch the run_tests argument for starting node.exe if not specified yet
                if (nodeArgs.Count == 0 && args.Count > 0)
                {
                    nodeArgs.Add(args[0]);
                }

                testObjects.Add(new TestCaseObject(framework: args[1], testName: args[2], testFile: args[3], workingFolder: args[4], projectFolder: args[5]));
            }

            var port = 0;
            if (runContext.IsBeingDebugged && startedFromVs)
            {
                this.DetachDebugger(vsProcessId);
                // Ensure that --debug-brk or --inspect-brk is the first argument
                nodeArgs.Insert(0, GetDebugArgs(nodeVersion, out port));
            }

            // make sure the testscompleted is not signalled.
            this.testsCompleted.Reset();

            this.nodeProcess = ProcessOutput.Run(
                settings.NodeExePath,
                nodeArgs,
                settings.WorkingDir,
                env: null,
                visible: false,
                redirector: new TestExecutionRedirector(this.ProcessTestRunnerEmit),
                quoteArgs: false);

            if (runContext.IsBeingDebugged && startedFromVs)
            {
                this.AttachDebugger(vsProcessId, port, nodeVersion);
            }


            // Send the process the list of tests to run and wait for it to complete
            this.nodeProcess.WriteInputLine(JsonConvert.SerializeObject(testObjects));

            // for node 8 the process doesn't automatically exit when debugging, so always detach
            WaitHandle.WaitAny(new[] { this.nodeProcess.WaitHandle, this.testsCompleted });
            if (runContext.IsBeingDebugged && startedFromVs)
            {
                this.DetachDebugger(vsProcessId);
            }

            // Automatically fail tests that haven't been run by this point (failures in before() hooks)
            foreach (var notRunTest in this.currentTests)
            {
                var result = new TestResult(notRunTest)
                {
                    Outcome = TestOutcome.Failed
                };

                if (this.currentResultObject != null)
                {
                    result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, this.currentResultObject.stdout));
                    result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, this.currentResultObject.stderr));
                }
                frameworkHandle.RecordResult(result);
                frameworkHandle.RecordEnd(notRunTest, TestOutcome.Failed);
            }
        }

        private void DetachDebugger(int vsProcessId)
        {
            VisualStudioApp.DetachDebugger(vsProcessId);
        }

        private void LogTelemetry(int testCount, Version nodeVersion, bool isDebugging, string testFramework)
        {
            VisualStudioApp.LogTelemetry(testCount, nodeVersion, isDebugging, testFramework);
        }

        private void AttachDebugger(int vsProcessId, int port, Version nodeVersion)
        {
            try
            {
                if (nodeVersion >= Node8Version)
                {
                    VisualStudioApp.AttachToProcessNode2DebugAdapter(vsProcessId, port);
                }
                else
                {
                    //the '#ping=0' is a special flag to tell VS node debugger not to connect to the port,
                    //because a connection carries the consequence of setting off --debug-brk, and breakpoints will be missed.
                    var qualifierUri = string.Format("tcp://localhost:{0}#ping=0", port);
                    while (!VisualStudioApp.AttachToProcess(this.nodeProcess.Process, vsProcessId, NodejsRemoteDebugPortSupplierUnsecuredId, qualifierUri))
                    {
                        if (this.nodeProcess.Wait(TimeSpan.FromMilliseconds(500)))
                        {
                            break;
                        }
                    }
                }
#if DEBUG
            }
            catch (COMException ex)
            {
                this.frameworkHandle.SendMessage(TestMessageLevel.Error, "Error occurred connecting to debuggee.");
                this.frameworkHandle.SendMessage(TestMessageLevel.Error, ex.ToString());
                KillNodeProcess();
            }
#else
            }
            catch (COMException)
            {
                frameworkHandle.SendMessage(TestMessageLevel.Error, "Error occurred connecting to debuggee.");
                KillNodeProcess();
            }
#endif
        }

        private void KillNodeProcess()
        {
            lock (this.syncObject)
            {
                this.nodeProcess?.Kill();
            }
        }

        private static int GetFreePort()
        {
            return Enumerable.Range(new Random().Next(49152, 65536), 60000).Except(
                from connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                select connection.LocalEndPoint.Port
            ).First();
        }

        private IEnumerable<string> GetInterpreterArgs(TestCase test, string workingDir, string projectRootDir)
        {
            var testInfo = new NodejsTestInfo(test.FullyQualifiedName, test.CodeFilePath);
            var testFramework = test.GetPropertyValue<string>(JavaScriptTestCaseProperties.TestFramework, defaultValue: null);
            return FrameworkDiscover.Intance.Get(testFramework).ArgumentsToRunTests(testInfo.TestName, testInfo.ModulePath, workingDir, projectRootDir);
        }

        private static string GetDebugArgs(Version nodeVersion, out int port)
        {
            port = GetFreePort();

            if (nodeVersion >= Node8Version)
            {
                return $"--inspect-brk={port}";
            }

            return $"--debug-brk={port}";
        }

        private NodejsProjectSettings LoadProjectSettings(string projectFile)
        {
            var env = new Dictionary<string, string>();

            var root = Environment.GetEnvironmentVariable(NodejsConstants.NodeToolsVsInstallRootEnvironmentVariable);
            if (string.IsNullOrEmpty(root))
            {
                root = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            }

            if (!string.IsNullOrEmpty(root))
            {
                env["VsInstallRoot"] = root;
                env["MSBuildExtensionsPath32"] = Path.Combine(root, "MSBuild");
            }

            var buildEngine = new MSBuild.ProjectCollection(env);
            var proj = buildEngine.LoadProject(projectFile);

            var projectRootDir = Path.GetFullPath(Path.Combine(proj.DirectoryPath, proj.GetPropertyValue(CommonConstants.ProjectHome) ?? "."));

            return new NodejsProjectSettings()
            {
                ProjectRootDir = projectRootDir,

                WorkingDir = Path.GetFullPath(Path.Combine(projectRootDir, proj.GetPropertyValue(CommonConstants.WorkingDirectory) ?? ".")),

                NodeExePath = Nodejs.GetAbsoluteNodeExePath(
                    projectRootDir,
                    proj.GetPropertyValue(NodeProjectProperty.NodeExePath))
            };
        }

        private void RecordEnd(IFrameworkHandle frameworkHandle, TestCase test, TestResult result, ResultObject resultObject)
        {
            var standardOutputLines = resultObject.stdout.Split('\n');
            var standardErrorLines = resultObject.stderr.Split('\n');

            if (resultObject.pending == true)
            {
                result.Outcome = TestOutcome.Skipped;
            }
            else
            {
                result.EndTime = DateTimeOffset.Now;
                result.Duration = result.EndTime - result.StartTime;
                result.Outcome = resultObject.passed ? TestOutcome.Passed : TestOutcome.Failed;
            }

            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, string.Join(Environment.NewLine, standardOutputLines)));
            result.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, string.Join(Environment.NewLine, standardErrorLines)));
            result.Messages.Add(new TestResultMessage(TestResultMessage.AdditionalInfoCategory, string.Join(Environment.NewLine, standardErrorLines)));
            frameworkHandle.RecordResult(result);
            frameworkHandle.RecordEnd(test, result.Outcome);
            this.currentTests.Remove(test);
        }

        private sealed class TestExecutionRedirector : Redirector
        {
            private readonly Action<string> writer;

            public TestExecutionRedirector(Action<string> onWriteLine)
            {
                this.writer = onWriteLine;
            }

            public override void WriteErrorLine(string line) => this.writer(line);

            public override void WriteLine(string line) => this.writer(line);

            public override bool CloseStandardInput() => false;
        }

        private sealed class TestReceiver : ITestCaseDiscoverySink
        {
            public readonly List<TestCase> Tests = new List<TestCase>();

            public void SendTestCase(TestCase discoveredTest)
            {
                this.Tests.Add(discoveredTest);
            }
        }
    }
}
