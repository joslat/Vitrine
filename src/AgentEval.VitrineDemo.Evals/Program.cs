// SPDX-License-Identifier: MIT
using AgentEval.VitrineDemo.Evals;
Console.OutputEncoding = System.Text.Encoding.UTF8;
return await EvaluationCli.RunAsync(args, Console.Out, Console.Error);
