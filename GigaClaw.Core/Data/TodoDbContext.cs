using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using GigaClaw.Core.Models;

namespace GigaClaw.Core.Data;

public class TodoDbContext : DbContext
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ActivityEntry> ActivityEntries => Set<ActivityEntry>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<ChatMessageRow> ChatMessages => Set<ChatMessageRow>();
    public DbSet<TicketDependency> TicketDependencies => Set<TicketDependency>();

    private readonly string _dbPath;

    /// <summary>Absolute path of the SQLite file — used as the MigrationGate memoization key.</summary>
    public string DbPath => _dbPath;

    public TodoDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            ForeignKeys = true
        }.ToString());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasMany(t => t.Comments).WithOne(c => c.Ticket).HasForeignKey(c => c.TicketId);
            e.HasMany(t => t.Activities).WithOne(a => a.Ticket).HasForeignKey(a => a.TicketId);
            e.HasMany(t => t.Labels).WithMany(l => l.Tickets).UsingEntity("TicketLabels");
        });

        modelBuilder.Entity<TicketDependency>(e =>
        {
            e.ToTable("TicketDependencies", table =>
                table.HasCheckConstraint(
                    "CK_TicketDependencies_NotSelf",
                    "BlockedTicketId <> BlockingTicketId"));
            e.HasKey(d => new { d.BlockedTicketId, d.BlockingTicketId });
            e.HasIndex(d => d.BlockingTicketId);
            e.HasOne(d => d.BlockedTicket)
                .WithMany(t => t.BlockedByEdges)
                .HasForeignKey(d => d.BlockedTicketId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.BlockingTicket)
                .WithMany(t => t.BlocksEdges)
                .HasForeignKey(d => d.BlockingTicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Comment>(e =>
        {
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<ActivityEntry>(e =>
        {
            e.HasKey(a => a.Id);
        });

        modelBuilder.Entity<Label>(e =>
        {
            e.HasKey(l => l.Id);
        });

        modelBuilder.Entity<BoardColumn>(e =>
        {
            e.HasKey(c => c.Id);
        });

        modelBuilder.Entity<Member>(e =>
        {
            e.HasKey(m => m.Id);
        });

        modelBuilder.Entity<ChatMessageRow>(e =>
        {
            e.HasKey(m => m.Id);
        });
    }
}
