using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace KJWebsite.BuildingBlocks.Outbox;

/// <summary>A side effect that must follow a database write. Stored in the same transaction as that write.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Routes the message to its <see cref="IOutboxHandler"/>, e.g. <c>cta.submission.received</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>JSON. Keep personal data out: carry ids and let the handler load what it needs.</summary>
    public string Payload { get; set; } = "{}";

    // UTC DateTime, not DateTimeOffset: Sqlite (tests) cannot translate DateTimeOffset comparisons.
    public DateTime CreatedAt { get; set; }

    public DateTime AvailableAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Lease held by the dispatcher that claimed the message; a crashed dispatcher's lease simply expires.</summary>
    public DateTime? LockedUntil { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>A message that exhausted its retries. Someone must own this table (see docs/OUTBOX.md).</summary>
public sealed class DeadLetterMessage
{
    public Guid Id { get; set; }

    public Guid OutboxMessageId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Payload { get; set; } = "{}";

    public int Attempts { get; set; }

    public string Error { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime FailedAt { get; set; }

    /// <summary>Set by whoever dealt with it; unresolved rows drive the alert.</summary>
    public DateTime? ResolvedAt { get; set; }
}

public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }

    DbSet<DeadLetterMessage> DeadLetters { get; }
}

public static class OutboxModelExtensions
{
    /// <summary>Maps the outbox tables into the context's own schema. Call from <c>OnModelCreating</c>.</summary>
    public static ModelBuilder ConfigureOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => new { x.ProcessedAt, x.AvailableAt });
        });

        modelBuilder.Entity<DeadLetterMessage>(entity =>
        {
            entity.ToTable("dead_letters");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(2000).IsRequired();
            entity.HasIndex(x => x.ResolvedAt);
        });

        return modelBuilder;
    }

    /// <summary>
    /// Adds a message to the context's change tracker. It is persisted by the caller's own <c>SaveChanges</c>, so the
    /// side effect commits or rolls back together with the business data and can neither be lost nor fire for a rolled-back write.
    /// </summary>
    public static OutboxMessage Enqueue<TPayload>(this IOutboxDbContext db, string type, TPayload payload, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            AvailableAt = now
        };
        db.OutboxMessages.Add(message);
        return message;
    }
}
