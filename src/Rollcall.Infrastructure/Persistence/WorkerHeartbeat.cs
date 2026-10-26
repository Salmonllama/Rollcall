// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Infrastructure.Persistence;

/// <summary>The last time a worker process proved it was alive. One row per worker.</summary>
public sealed class WorkerHeartbeat
{
    public required string WorkerId { get; set; }

    public DateTimeOffset BeatAt { get; set; }
}
