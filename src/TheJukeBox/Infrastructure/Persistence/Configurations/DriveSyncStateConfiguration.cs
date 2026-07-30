using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence.Configurations;

internal sealed class DriveSyncStateConfiguration : IEntityTypeConfiguration<DriveSyncState>
{
    public void Configure(EntityTypeBuilder<DriveSyncState> builder)
    {
        builder.ToTable("DriveSyncStates");

        builder.HasKey(state => state.UserId);
        builder.Property(state => state.UserId).ValueGeneratedNever();

        builder.HasOne(state => state.User)
            .WithOne(user => user.SyncState)
            .HasForeignKey<DriveSyncState>(state => state.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Drive's page tokens are opaque and undocumented in length; observed values
        // are well under 100 characters.
        builder.Property(state => state.ChangesStartPageToken).HasMaxLength(512);

        // Stored as the enum member's name. See the remarks on SyncStatus for why
        // the ordinal is the wrong choice here.
        builder.Property(state => state.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(state => state.Phase).HasMaxLength(64);

        // Truncated deliberately: this is displayed to the user and read by an
        // operator, and an unbounded exception message from a failing sync would
        // otherwise grow the row without adding information.
        builder.Property(state => state.LastError).HasMaxLength(1024);
        builder.Property(state => state.LastErrorType).HasMaxLength(128);

        builder.Property(state => state.ItemsProcessed).IsRequired();
        builder.Property(state => state.ItemsTotal).IsRequired();
        builder.Property(state => state.ConsecutiveFailures).IsRequired();
    }
}
