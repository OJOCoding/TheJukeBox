using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class DriveTokenConfiguration : IEntityTypeConfiguration<DriveToken>
{
    public void Configure(EntityTypeBuilder<DriveToken> builder)
    {
        builder.ToTable("DriveTokens");

        // The foreign key *is* the primary key, which is what makes "one grant per
        // user" a schema guarantee rather than a convention.
        builder.HasKey(token => token.UserId);
        builder.Property(token => token.UserId).ValueGeneratedNever();

        builder.HasOne(token => token.User)
            .WithOne(user => user.DriveToken)
            .HasForeignKey<DriveToken>(token => token.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Data Protection payloads are base64url and carry a 32-byte header plus
        // padding, so a ~512-character Google refresh token lands near 1 KiB. 4096
        // leaves room for a future protector with a larger envelope without a
        // migration, and is still small enough that the column is never a surprise.
        builder.Property(token => token.EncryptedRefreshToken)
            .IsRequired()
            .HasMaxLength(4096);

        builder.Property(token => token.Scopes)
            .IsRequired()
            .HasMaxLength(1024);

        builder.Property(token => token.GrantedAtUtc).IsRequired();
        builder.Property(token => token.LastErrorCode).HasMaxLength(128);

        // Computed from RevokedAtUtc. EF would not map an expression-bodied
        // getter by convention, but saying so explicitly means adding a setter later
        // cannot quietly turn it into a column that disagrees with its own inputs.
        builder.Ignore(token => token.IsUsable);
    }
}
