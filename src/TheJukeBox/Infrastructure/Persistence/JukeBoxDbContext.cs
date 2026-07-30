using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// The single database context for this instance.
/// </summary>
public sealed class JukeBoxDbContext(DbContextOptions<JukeBoxDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<DriveToken> DriveTokens => Set<DriveToken>();

    public DbSet<DriveSyncState> DriveSyncStates => Set<DriveSyncState>();

    public DbSet<Track> Tracks => Set<Track>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        // Configuration lives in IEntityTypeConfiguration<T> classes rather than in
        // attributes on the entities: mapping concerns (column lengths, index names,
        // delete behaviour) are infrastructure, and keeping them out of Domain means
        // the entities stay readable as a description of the problem.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(JukeBoxDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // Applied to every DateTimeOffset (and DateTimeOffset?) in the model. See
        // UtcDateTimeOffsetConverter: on SQLite this is what makes ORDER BY on a
        // timestamp column correct.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
