using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class TrackConfiguration : IEntityTypeConfiguration<Track>
{
    public void Configure(EntityTypeBuilder<Track> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Tracks");

        builder.HasKey(t => t.Id);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(t => t.DriveFileId).IsRequired().HasMaxLength(256);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(1024);
        builder.Property(t => t.MimeType).HasMaxLength(128);
        builder.Property(t => t.Md5Checksum).HasMaxLength(32);
        builder.Property(t => t.HeadRevisionId).HasMaxLength(256);
        builder.Property(t => t.ParentFolderId).HasMaxLength(256);
        builder.Property(t => t.ParentFolderName).HasMaxLength(1024);
        builder.Property(t => t.FileExtension).HasMaxLength(16);

        builder.Property(t => t.Title).IsRequired().HasMaxLength(1024);
        builder.Property(t => t.Artist).HasMaxLength(512);
        builder.Property(t => t.Album).HasMaxLength(512);
        builder.Property(t => t.AlbumArtist).HasMaxLength(512);
        builder.Property(t => t.Genre).HasMaxLength(128);
        builder.Property(t => t.ArtSha256).HasMaxLength(64);
        builder.Property(t => t.SearchText).IsRequired().HasMaxLength(2048);
        builder.Property(t => t.MetadataFingerprint).HasMaxLength(128);

        builder.Property(t => t.MetadataSource).HasConversion<string>().HasMaxLength(16);

        builder.Ignore(t => t.IsAvailable);

        // The pair, not DriveFileId alone: a file shared with two users legitimately
        // appears in both libraries, and a global constraint would make the second
        // sync fail.
        builder.HasIndex(t => new { t.UserId, t.DriveFileId })
            .IsUnique()
            .HasDatabaseName("IX_Tracks_UserId_DriveFileId");

        // Every browse query is scoped to one user and excludes soft-deleted rows, so
        // both columns lead. Without DeletedAtUtc in the index SQLite would have to
        // filter after seeking, which on a large library is the difference between an
        // index scan and a table scan.
        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.Title })
            .HasDatabaseName("IX_Tracks_Browse_Title");

        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.Artist })
            .HasDatabaseName("IX_Tracks_Browse_Artist");

        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.Album, t.DiscNumber, t.TrackNumber })
            .HasDatabaseName("IX_Tracks_Browse_Album");

        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.AddedAtUtc })
            .HasDatabaseName("IX_Tracks_Browse_Added");

        // Delta sync: "everything above version N for this user". Includes deleted rows
        // deliberately, because a client needs to be told about removals too.
        builder.HasIndex(t => new { t.UserId, t.RowVersion })
            .HasDatabaseName("IX_Tracks_RowVersion");

        // The two listening sorts, indexed like the other browse orders so "most played"
        // and "recently played" are seeks rather than full sorts of the library.
        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.PlayCount })
            .HasDatabaseName("IX_Tracks_Browse_PlayCount");

        builder.HasIndex(t => new { t.UserId, t.DeletedAtUtc, t.LastPlayedAtUtc })
            .HasDatabaseName("IX_Tracks_Browse_LastPlayed");
    }
}
