using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class PlaylistConfiguration : IEntityTypeConfiguration<Playlist>
{
    public void Configure(EntityTypeBuilder<Playlist> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Playlists");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(1000);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsRequired();

        // Deliberately not unique on (UserId, Name). Two playlists called "Favourites" is
        // the user's business, and a uniqueness error on a rename is a worse experience than
        // a duplicate name they can see and fix.
        builder.HasIndex(p => new { p.UserId, p.UpdatedAtUtc })
            .HasDatabaseName("IX_Playlists_UserId_UpdatedAtUtc");

        builder.HasMany(p => p.Items)
            .WithOne(i => i.Playlist)
            .HasForeignKey(i => i.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlaylistItemConfiguration : IEntityTypeConfiguration<PlaylistItem>
{
    public void Configure(EntityTypeBuilder<PlaylistItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PlaylistItems");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        // Cascade from the track as well as from the playlist. Tracks are soft-deleted in
        // normal operation, so this only fires if a row is genuinely removed -- but without
        // it, that removal would leave an item pointing at nothing and every read would have
        // to defend against a null join.
        builder.HasOne(i => i.Track)
            .WithMany()
            .HasForeignKey(i => i.TrackId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(i => i.Position).IsRequired();
        builder.Property(i => i.AddedAtUtc).IsRequired();

        // Unique, because a duplicated position makes the playlist's order ambiguous and the
        // bug would show up as tracks quietly swapping places. This is the constraint that
        // turns "renumber correctly" from an intention into a rule the database enforces.
        builder.HasIndex(i => new { i.PlaylistId, i.Position })
            .IsUnique()
            .HasDatabaseName("IX_PlaylistItems_PlaylistId_Position");

        // "Which playlists is this track in?", for the add-to-playlist affordance.
        builder.HasIndex(i => i.TrackId).HasDatabaseName("IX_PlaylistItems_TrackId");
    }
}
