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
    ///   The base selector class. This will attempt to execute all branches of logic, until one succeeds. 
    ///   This composite will fail only if all branches fail as well.
    /// </summary>
    public abstract class Selector : GroupComposite
    {
        public Selector(params Composite[] children) : base(children)
        {
        }

        protected abstract override IEnumerable<RunStatus> Execute(object context);
    }

    public class ProbabilitySelection
    {
        public Composite Branch;

        public double ChanceToExecute;

        public ProbabilitySelection(Composite branch, double chanceToExecute)
        {
            Branch = branch;
            ChanceToExecute = chanceToExecute;
        }
    }

    /// <summary>
    ///   Will execute random branches of logic, until one succeeds. This composite
    ///   will fail only if all branches fail as well.
    /// </summary>
    public class ProbabilitySelector : Selector
    {
        public ProbabilitySelector(params Composite[] children) : base(children)
        {
            Randomizer = new Random();
        }

        protected Random Randomizer { get; private set; }

        protected override IEnumerable<RunStatus> Execute(object context)
        {
            if (ContextChanger != null)
            {
                context = ContextChanger(context);
            }

            if (Children.Count == 0)
            {
                yield return RunStatus.Failure;
                yield break;
            }

            // Create a list of children to randomly select from
            List<Composite> remainingChildren = Children.ToList();
            Composite? current = remainingChildren[Randomizer.Next(remainingChildren.Count)];
            remainingChildren.Remove(current);

            while (true)
            {
                if (current != null)
                {
                    current.Start(context);

                    while (current.Tick(context) == RunStatus.Running)
                    {
                        Selection = current;
                        yield return RunStatus.Running;
                    }

                    Selection = null;
                    current.Stop(context);

                    if (current.LastStatus == RunStatus.Success)
                    {
                        yield return RunStatus.Success;
                        yield break;
                    }
                }

                if (remainingChildren.Count == 0)
                {
                    yield return RunStatus.Failure;
                    yield break;
                }

                current = remainingChildren[Randomizer.Next(remainingChildren.Count)];
                remainingChildren.Remove(current);
            }
        }
    }
}