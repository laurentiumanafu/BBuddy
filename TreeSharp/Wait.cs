using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TreeSharp
{
    public class Wait : Decorator
    {
        private TimeSpan? _timeout;
        private WaitGetTimeoutDelegate _timeoutRetriever;
        protected DateTime End;

        public TimeSpan Timeout
        {
            get
            {
                return _timeout.HasValue ? _timeout.Value : new TimeSpan(0, 0, _timeoutRetriever());
            }
            set => _timeout = value;
        }

        [DebuggerStepThrough]
        public Wait(TimeSpan timeout, CanRunDecoratorDelegate runFunc, Composite child)
            : base(child, runFunc)
        {
            Timeout = timeout;
        }

        [DebuggerStepThrough]
        public Wait(int timeoutSeconds, CanRunDecoratorDelegate runFunc, Composite child)
            : base(child, runFunc)
        {
            Timeout = new TimeSpan(0, 0, timeoutSeconds);
        }

        [DebuggerStepThrough]
        public Wait(int timeoutSeconds, Composite child)
            : base(child, null)
        {
            Timeout = new TimeSpan(0, 0, timeoutSeconds);
        }

        [DebuggerStepThrough]
        public Wait(TimeSpan timeout, Composite child)
            : base(child, null)
        {
            Timeout = timeout;
        }

        public Wait(WaitGetTimeoutDelegate timeoutRetriever, Composite child)
            : base(child)
        {
            _timeoutRetriever = timeoutRetriever;
        }

        public Wait(WaitGetTimeoutDelegate timeoutRetriever, CanRunDecoratorDelegate runFunc, Composite child)
            : base(child, runFunc)
        {
            _timeoutRetriever = timeoutRetriever;
        }

        [DebuggerStepThrough]
        public override void Start(object context)
        {
            End = DateTime.Now.Add(Timeout);
            base.Start(context);
        }

        [DebuggerStepThrough]
        public override void Stop(object context)
        {
            End = DateTime.MinValue;
            base.Stop(context);
        }

        [DebuggerStepThrough]
        protected override IEnumerable<RunStatus> Execute(object context)
        {
            DateTime now;
            while ((now = DateTime.Now) < End)
            {
                if (Runner != null)
                {
                    if (Runner(context))
                        break;
                }
                else if (CanRun(context))
                    break;
                yield return RunStatus.Running;
            }
            if (now >= End)
                yield return RunStatus.Failure;
            else if (DecoratedChild == null)
            {
                yield return RunStatus.Success;
            }
            else
            {
                DecoratedChild.Start(context);
                while (DecoratedChild.Tick(context) == RunStatus.Running)
                    yield return RunStatus.Running;
                DecoratedChild.Stop(context);
                RunStatus? lastStatus = DecoratedChild.LastStatus;
                if (lastStatus.HasValue && lastStatus.Value == RunStatus.Failure)
                    yield return RunStatus.Failure;
                else
                    yield return RunStatus.Success;
            }
        }
    }
}
