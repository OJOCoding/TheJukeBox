using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class AlbumArtConfiguration : IEntityTypeConfiguration<AlbumArt>
{
    public void Configure(EntityTypeBuilder<AlbumArt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AlbumArt");

        // The hash is the key. No surrogate id, because a surrogate would permit two rows
        // holding the same image, which is the exact thing this table exists to prevent.
        builder.HasKey(a => a.Sha256);

        builder.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(a => a.MimeType).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Bytes).IsRequired();

        // No foreign key to Tracks. Art outlives the track that introduced it: a soft
        // deleted track still renders in history and in playlists, and a cascade from any
        // one track would take the shared album cover away from its eleven siblings.
        // Orphans are harmless and cheap, and pruning them is a maintenance job, not a
        // constraint.
    }
}
