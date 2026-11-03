using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TreeSharp
{
    public class Action : Composite
    {
        public ActionDelegate? Runner { get; private set; }
        public ActionSucceedDelegate? SucceedRunner { get; private set; }

        public Action() { }

        public Action(ActionDelegate action)
        {
            this.Runner = action;
        }

        public Action(ActionSucceedDelegate action)
        {
            this.SucceedRunner = action;
        }

        protected new virtual RunStatus Run(object context)
        {
            return RunStatus.Failure;
        }

        [DebuggerStepThrough]
        protected RunStatus RunAction(object context)
        {
            if (Runner != null)
            {
                return Runner(context);
            }
            else if (SucceedRunner != null)
            {
                SucceedRunner(context);
                return RunStatus.Success;
            }
            else
            {
                return Run(context);
            }
        }

        [DebuggerStepThrough]
        protected sealed override IEnumerable<RunStatus> Execute(object context)
        {
            // HB 4.3.4: Loop while action returns Running, then yield final status
            while (true)
            {
                RunStatus? nullable = this.LastStatus = new RunStatus?(this.RunAction(context));
                if ((nullable.GetValueOrDefault() != RunStatus.Running ? 0 : (nullable.HasValue ? 1 : 0)) != 0)
                    yield return RunStatus.Running;
                else
                    break;
            }
            yield return this.LastStatus.Value;
        }
    }
}
