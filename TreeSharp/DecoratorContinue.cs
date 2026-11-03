using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TreeSharp
{
    /// <summary>
    /// DecoratorContinue - Exact HB WoD/MoP implementation
    /// </summary>
    public class DecoratorContinue : Decorator
    {
        [DebuggerStepThrough]
        public DecoratorContinue(Composite decorated, CanRunDecoratorDelegate func) : base(decorated, func) { }
        
        [DebuggerStepThrough]
        public DecoratorContinue(CanRunDecoratorDelegate func, Composite decorated) : base(func, decorated) { }
        
        [DebuggerStepThrough]
        public DecoratorContinue(Composite child) : base(child) { }
        
        [DebuggerStepThrough]
        public DecoratorContinue() { }

        protected override IEnumerable<RunStatus> Execute(object context)
        {
            if (!this.CanRun(context))
            {
                yield return RunStatus.Success;
            }
            else
            {
                base.DecoratedChild.Start(context);
                while (base.DecoratedChild.Tick(context) == RunStatus.Running)
                {
                    yield return RunStatus.Running;
                }
                base.DecoratedChild.Stop(context);
                if (base.DecoratedChild.LastStatus == RunStatus.Failure)
                {
                    yield return RunStatus.Failure;
                }
                else
                {
                    yield return RunStatus.Success;
                }
            }
            yield break;
        }
    }
}
