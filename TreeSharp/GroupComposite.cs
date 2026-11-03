
using System.Collections.Generic;

namespace TreeSharp
{
    public abstract class GroupComposite : Composite
    {
        protected GroupComposite(params Composite[] children)
        {
            Children = new List<Composite>(children);
            foreach (Composite composite in Children)
            {
                if (composite != null)
                {
                    composite.Parent = this;
                }
            }
        }

        public new List<Composite> Children { get; set; }

        public Composite? Selection { get; protected set; }

        public override void Start(object context)
        {
            CleanupHandlers.Push(new ChildrenCleanupHandler(this, context));
            base.Start(context);
        }

        protected class ChildrenCleanupHandler : CleanupHandler
        {
            public ChildrenCleanupHandler(GroupComposite owner, object context) 
                : base(owner, context)
            {
            }

            protected override void DoCleanup(object context)
            {
                var owner = Owner as GroupComposite;
                if (owner?.Children == null)
                    return;
                    
                foreach (Composite child in owner.Children)
                {
                    if (child != null)
                        child.Stop(context);
                }
            }
        }

        public void AddChild(Composite child)
        {
            if (child != null)
            {
                child.Parent = this;
                Children.Add(child);
            }
        }

        public void InsertChild(int index, Composite child)
        {
            if (child != null)
            {
                child.Parent = this;
                Children.Insert(index, child);
            }
        }
    }
}