#region License

// A simplistic Behavior Tree implementation in C#
// Copyright (C) 2010-2011 ApocDev apocdev@gmail.com
// 
// This file is part of TreeSharp
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

#endregion

using System;
using System.Collections.Generic;
using System.Linq;

namespace TreeSharp
{
    /// <summary>
    ///   This composite will perform a 'switch' statement to execute a specific branch of logic.
    ///   This is useful for selecting specific branches, for different types of agents. (e.g. rogue, mage, and warrior branches)
    /// </summary>
    /// <typeparam name = "T"></typeparam>
    public class Switch<T> : GroupComposite
    {
        // Context-less variant: wraps as ctx => statement()
        public Switch(Func<T> statement, params SwitchArgument<T>[] args) : base(args.Select(a => a.Branch).ToArray())
        {
            Statement = _ => statement();
            Arguments = args;
        }

        public Switch(Func<T> statement, Composite defaultArgument, params SwitchArgument<T>[] args) : this(statement, args)
        {
            Default = defaultArgument;
        }

        // HB 4.3.4+ compatibility: Quest Behaviors use RetrieveSwitchParameterDelegate<T>(object context)
        // The context from the parent composite is passed through — do NOT hardcode null.
        public Switch(RetrieveSwitchParameterDelegate<T> statement, params SwitchArgument<T>[] args)
            : base(args.Select(a => a.Branch).ToArray())
        {
            Statement = ctx => statement(ctx);
            Arguments = args;
        }

        public Switch(RetrieveSwitchParameterDelegate<T> statement, Composite defaultArgument, params SwitchArgument<T>[] args)
            : this(statement, args)
        {
            Default = defaultArgument;
        }

        /// <summary>
        ///   The statement assigned to this Switch that will determine which logical branch to take.
        ///   Takes the tree context as its argument (matches HB 4.3.4 RetrieveSwitchParameterDelegate behaviour).
        /// </summary>
        protected Func<object, T> Statement { get; set; }

        /// <summary>
        ///   The switch arguments.
        /// </summary>
        protected SwitchArgument<T>[] Arguments { get; set; }

        /// <summary>
        ///   The 'default' argument to be carried out if no other switch conditions are met.
        /// </summary>
        protected Composite Default { get; set; }

        protected void RunSwitch(object context)
        {
            if (Arguments == null && Default == null)
            {
                throw new NullReferenceException("Switch statement has no arguments, and no default statement. Can not run.");
            }

            if (Statement == null)
            {
                throw new NullReferenceException("Switch statement is null.");
            }

            // Run the statement with the current tree context (matches HB 4.3.4 behaviour).
            T value = Statement(context);

            if (Arguments != null)
            {
                SwitchArgument<T>? arg = Arguments.FirstOrDefault(a => a.RequiredValue.Equals(value));
                if (arg != null)
                {
                    Selection = arg.Branch;
                    return;
                }
            }

            if (Default != null)
            {
                Selection = Default;
            }
        }

        protected override IEnumerable<RunStatus> Execute(object context)
        {
            RunSwitch(context);

            if (Selection == null)
            {
                // Match HB 4.3.4 / 6.2.3 reference behaviour: when no argument matches and no
                // default branch is supplied, return Failure so the parent composite can pick
                // another branch. Throwing here stalls the tree (e.g. PartyBot "Moving to leader").
                yield return RunStatus.Failure;
                yield break;
            }

            Selection.Start(context);
            while (Selection.Tick(context) == RunStatus.Running)
            {
                yield return RunStatus.Running;
            }

            Selection.Stop(context);

            if (Selection.LastStatus == RunStatus.Failure)
            {
                yield return RunStatus.Failure;
                yield break;
            }

            yield return RunStatus.Success;
            yield break;
        }
    }
}