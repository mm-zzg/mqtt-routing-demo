using Microsoft.EntityFrameworkCore;

namespace MqttRouting.ClientSimulator.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<CertificateEntity> Certificates => Set<CertificateEntity>();
    public DbSet<ClientConfigEntity> ClientConfigs => Set<ClientConfigEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CertificateEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(256);
            e.Property(c => c.PfxBase64).IsRequired();
            e.Property(c => c.Password).IsRequired().HasMaxLength(256);
        });

        modelBuilder.Entity<ClientConfigEntity>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).IsRequired().HasMaxLength(256);
            e.Property(c => c.BrokerHost).IsRequired().HasMaxLength(256);
            e.Property(c => c.Topic).IsRequired().HasMaxLength(512);
            e.HasOne(c => c.Certificate)
             .WithMany()
             .HasForeignKey(c => c.CertificateId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
