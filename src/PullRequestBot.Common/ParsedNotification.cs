using System;
using System.Collections.Generic;

namespace PullRequestBot.Common
{
    public class ParsedNotification
    {
        public string EventType { get; set; }
        public Resource Resource { get; set; }
        public bool RunBatmonUrlActor { get; set; } = false;
        public bool RunAllActors { get; set; } = true;
    }

    public class Resource
    {
        public string MergeStatus { get; set; }
        public string Status { get; set; }
        public int PullRequestId { get; set; }
        public List<RefUpdates> RefUpdates { get; set; }
        public Repository Repository { get; set; }
    }

    public class Repository
    {
        public Guid Id { get; set; }
        public Uri Url { get; set; }
        public Project Project { get; set; }
    }

    public class Project
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public class RefUpdates
    {
        public string Name { get; set; }
    }
}
