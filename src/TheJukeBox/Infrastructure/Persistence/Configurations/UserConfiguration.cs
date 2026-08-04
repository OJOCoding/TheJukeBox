using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        // Ids are generated in the application (UUIDv7, so they still cluster by
        // creation time in the index). Letting the database generate them would mean
        // an entity has no identity until SaveChanges, which complicates every
        // "create the user and their sync state together" path in A4.
        builder.Property(user => user.Id).ValueGeneratedNever();

        // 255 is the maximum length OpenID Connect permits for a subject identifier.
        // Google's values are ~21 digits today; sizing to the spec rather than to
        // observed data means a change on their side is not a migration on ours.
        builder.Property(user => user.GoogleSubjectId)
            .IsRequired()
            .HasMaxLength(255);

        // Unique, because this is the account key. Without the index a race between
        // two simultaneous first sign-ins for the same Google account creates two
        // users, and the loser's library silently disappears on the next sign-in.
        builder.HasIndex(user => user.GoogleSubjectId)
            .IsUnique()
            .HasDatabaseName("IX_Users_GoogleSubjectId");

        // 320 = 64-character local part + '@' + 255-character domain, the longest
        // address the SMTP grammar allows.
        builder.Property(user => user.Email).HasMaxLength(320);
        builder.Property(user => user.DisplayName).HasMaxLength(200);
        builder.Property(user => user.PictureUrl).HasMaxLength(2048);

        builder.Property(user => user.SecurityStamp)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(user => user.CreatedAtUtc).IsRequired();
        builder.Property(user => user.LastSeenAtUtc).IsRequired();
        builder.Property(user => user.IsAdmin).IsRequired();

        builder.Property(user => user.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Ignore(user => user.IsActive);

        // The admin screen lists pending accounts, and session validation reads status on
        // every request.
        builder.HasIndex(user => user.Status).HasDatabaseName("IX_Users_Status");
    }
}
