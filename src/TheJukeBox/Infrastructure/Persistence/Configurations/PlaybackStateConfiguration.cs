using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class PlaybackStateConfiguration : IEntityTypeConfiguration<PlaybackState>
{
    public void Configure(EntityTypeBuilder<PlaybackState> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PlaybackStates");

        // The user id *is* the key. A surrogate key would permit two resume points for one
        // user, and then something would have to decide which is real.
        builder.HasKey(s => s.UserId);
        builder.Property(s => s.UserId).ValueGeneratedNever();

        builder.HasOne(s => s.User)
            .WithOne()
            .HasForeignKey<PlaybackState>(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // 32 hex characters per id, so the column is sized from the cap rather than guessed.
        builder.Property(s => s.QueuedTrackIds)
            .IsRequired()
            .HasMaxLength(PlaybackState.MaxQueueLength * 32);

        builder.Property(s => s.Repeat).HasConversion<string>().HasMaxLength(8).IsRequired();

        builder.Property(s => s.UpdatedAtUtc).IsRequired();
    }
}

internal sealed class PlayHistoryConfiguration : IEntityTypeConfiguration<PlayHistory>
{
    public void Configure(EntityTypeBuilder<PlayHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PlayHistory");

        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();

        builder.HasOne(h => h.User)
            .WithMany()
            .HasForeignKey(h => h.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(h => h.Track)
            .WithMany()
            .HasForeignKey(h => h.TrackId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(h => h.PlayedAtUtc).IsRequired();

        // Every read is "this user's plays, newest first", and the prune is "this user's
        // oldest". Both are served by the same descending-capable index.
        builder.HasIndex(h => new { h.UserId, h.PlayedAtUtc })
            .HasDatabaseName("IX_PlayHistory_UserId_PlayedAtUtc");
    }
}
