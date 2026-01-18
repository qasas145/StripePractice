using Microsoft.EntityFrameworkCore;
using StripePractice.Models;

namespace StripePractice.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Plan> Plans { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.Property(u => u.Email).IsRequired();
            });

            modelBuilder.Entity<Plan>(entity =>
            {
                entity.HasKey(p => p.Id);
                entity.Property(p => p.Name).IsRequired();
                entity.HasIndex(p => p.Name).IsUnique();
            });

            // Seed basic plans with placeholder Stripe Price IDs (update in DB later)
            modelBuilder.Entity<Plan>().HasData(
                new Plan { Id = 1, Name = "Basic", MonthlyEmailLimit = 1000, StripePriceId = "price_basic_placeholder" },
                new Plan { Id = 2, Name = "Premium", MonthlyEmailLimit = 10000, StripePriceId = "price_premium_placeholder" },
                new Plan { Id = 3, Name = "FreeTrial", MonthlyEmailLimit = 200, StripePriceId = null }
            );
        }
    }
}
