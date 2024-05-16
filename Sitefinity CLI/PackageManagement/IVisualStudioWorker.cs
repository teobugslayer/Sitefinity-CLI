﻿using System;
using System.Collections.Generic;

namespace Sitefinity_CLI.PackageManagement
{
    public interface IVisualStudioWorker : IDisposable
    {
        void Initialize(string solutionFilePath);

        void ExecuteScript(string scriptPath, List<string> scriptParameters);
    }
}
