using Microsoft.EntityFrameworkCore;
using SmartBank.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace SmartBank.Infrastructure.Data
{
    public class SmartBankDbContext : DbContext
    {
        public SmartBankDbContext(DbContextOptions<SmartBankDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<MarketRate> MarketRates { get; set; }
        public DbSet<Account> Accounts { get; set; }
        public DbSet<Transaction> Transactions { get; set; }
        public DbSet<ChatSession> ChatSessions { get; set; }
        public DbSet<ChatMessage> ChatMessages { get; set; }
        public DbSet<CreditCard> CreditCards { get; set; }
        public DbSet<CreditCardStatement> CreditCardStatements { get; set; }
        public DbSet<CreditCardTransaction> CreditCardTransactions { get; set; }
        public DbSet<SavedContact> SavedContacts { get; set; }
        public DbSet<StandingOrder> StandingOrders { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // User Configuration
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.Property(u => u.Username).IsRequired().HasMaxLength(50);
                entity.Property(u => u.Tckn).IsRequired().HasMaxLength(11);
                entity.Property(u => u.PasswordHash).IsRequired();
                entity.Property(u => u.FullName).IsRequired().HasMaxLength(100);
                entity.Property(u => u.Email).IsRequired().HasMaxLength(100);
                entity.Property(u => u.TwoFactorSecret).HasMaxLength(10);
                
                entity.HasIndex(u => u.Username).IsUnique();
                entity.HasIndex(u => u.Tckn).IsUnique();
                entity.HasIndex(u => u.Email).IsUnique();
            });

            // Account Configuration
            modelBuilder.Entity<Account>(entity =>
            {
                entity.HasKey(a => a.Id);
                entity.Property(a => a.AccountNumber).IsRequired().HasMaxLength(20);
                entity.Property(a => a.AccountCode).IsRequired().HasMaxLength(15);
                entity.Property(a => a.Balance).HasColumnType("decimal(18,2)");
                entity.Property(a => a.Currency).IsRequired().HasMaxLength(3);
                entity.Property(a => a.EncryptedCardNumber).IsRequired().HasMaxLength(100);
                entity.Property(a => a.EncryptedCardCvv).IsRequired().HasMaxLength(50);
                entity.Property(a => a.CardTheme).IsRequired().HasMaxLength(50);

                entity.HasIndex(a => a.AccountNumber).IsUnique();
                entity.HasIndex(a => a.AccountCode).IsUnique();

                entity.HasOne(a => a.User)
                      .WithMany(u => u.Accounts)
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Transaction Configuration
            modelBuilder.Entity<Transaction>(entity =>
            {
                entity.HasKey(t => t.Id);
                entity.Property(t => t.Amount).HasColumnType("decimal(18,2)");
                entity.Property(t => t.Description).HasMaxLength(200);
                entity.Property(t => t.Type).HasConversion<string>(); // Save enum as string
                entity.Property(t => t.Category).HasMaxLength(50).HasDefaultValue("Diğer");

                // Configure relationship with SourceAccount
                entity.HasOne(t => t.SourceAccount)
                      .WithMany(a => a.SentTransactions)
                      .HasForeignKey(t => t.SourceAccountId)
                      .OnDelete(DeleteBehavior.Restrict); // Prevent multiple cascade paths

                // Configure relationship with DestinationAccount
                entity.HasOne(t => t.DestinationAccount)
                      .WithMany(a => a.ReceivedTransactions)
                      .HasForeignKey(t => t.DestinationAccountId)
                      .OnDelete(DeleteBehavior.Restrict); // Prevent multiple cascade paths
            });

            // ChatSession Configuration
            modelBuilder.Entity<ChatSession>(entity =>
            {
                entity.HasKey(cs => cs.Id);
                entity.Property(cs => cs.Title).HasMaxLength(100);

                entity.HasOne(cs => cs.User)
                      .WithMany(u => u.ChatSessions)
                      .HasForeignKey(cs => cs.UserId)
                      .OnDelete(DeleteBehavior.SetNull); // Keep chat history even if user is deleted
            });

            // ChatMessage Configuration
            modelBuilder.Entity<ChatMessage>(entity =>
            {
                entity.HasKey(cm => cm.Id);
                entity.Property(cm => cm.Sender).IsRequired().HasMaxLength(20);
                entity.Property(cm => cm.Content).IsRequired();

                entity.HasOne(cm => cm.Session)
                      .WithMany(s => s.Messages)
                      .HasForeignKey(cm => cm.SessionId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // CreditCard Configuration
            modelBuilder.Entity<CreditCard>(entity =>
            {
                entity.HasKey(cc => cc.Id);
                entity.Property(cc => cc.EncryptedCardNumber).IsRequired().HasMaxLength(100);
                entity.Property(cc => cc.EncryptedCardCvv).IsRequired().HasMaxLength(50);
                entity.Property(cc => cc.ExpiryDate).IsRequired().HasMaxLength(10);
                entity.Property(cc => cc.CardLimit).HasColumnType("decimal(18,2)");
                entity.Property(cc => cc.CurrentDebt).HasColumnType("decimal(18,2)");
                entity.Property(cc => cc.CardTheme).IsRequired().HasMaxLength(50);

                entity.HasOne(cc => cc.User)
                      .WithMany(u => u.CreditCards)
                      .HasForeignKey(cc => cc.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // CreditCardStatement Configuration
            modelBuilder.Entity<CreditCardStatement>(entity =>
            {
                entity.HasKey(ccs => ccs.Id);
                entity.Property(ccs => ccs.PeriodName).IsRequired().HasMaxLength(50);
                entity.Property(ccs => ccs.PeriodDebt).HasColumnType("decimal(18,2)");
                entity.Property(ccs => ccs.MinimumPayment).HasColumnType("decimal(18,2)");
                entity.Property(ccs => ccs.PaidAmount).HasColumnType("decimal(18,2)");

                entity.HasOne(ccs => ccs.CreditCard)
                      .WithMany(cc => cc.Statements)
                      .HasForeignKey(ccs => ccs.CreditCardId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // CreditCardTransaction Configuration
            modelBuilder.Entity<CreditCardTransaction>(entity =>
            {
                entity.HasKey(cct => cct.Id);
                entity.Property(cct => cct.Description).IsRequired().HasMaxLength(200);
                entity.Property(cct => cct.Amount).HasColumnType("decimal(18,2)");

                entity.HasOne(cct => cct.CreditCard)
                      .WithMany(cc => cc.Transactions)
                      .HasForeignKey(cct => cct.CreditCardId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // SavedContact Configuration
            modelBuilder.Entity<SavedContact>(entity =>
            {
                entity.HasKey(sc => sc.Id);
                entity.Property(sc => sc.AccountNumber).IsRequired().HasMaxLength(50);
                entity.Property(sc => sc.Alias).IsRequired().HasMaxLength(100);

                entity.HasOne(sc => sc.User)
                      .WithMany()
                      .HasForeignKey(sc => sc.UserId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // MarketRate Configuration
            modelBuilder.Entity<MarketRate>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Code).IsRequired().HasMaxLength(10);
                entity.Property(m => m.Buy).HasColumnType("decimal(18,4)");
                entity.Property(m => m.Sell).HasColumnType("decimal(18,4)");
            });

            // StandingOrder Configuration
            modelBuilder.Entity<StandingOrder>(entity =>
            {
                entity.HasKey(so => so.Id);
                entity.Property(so => so.SourceAccountNumber).IsRequired().HasMaxLength(50);
                entity.Property(so => so.DestinationAccountNumber).HasMaxLength(50);
                entity.Property(so => so.Amount).HasColumnType("decimal(18,2)");
                entity.Property(so => so.Frequency).IsRequired().HasMaxLength(20);
                entity.Property(so => so.OrderType).IsRequired().HasMaxLength(20);

                entity.HasOne(so => so.User)
                      .WithMany()
                      .HasForeignKey(so => so.UserId)
                      .OnDelete(DeleteBehavior.NoAction);

                entity.HasOne(so => so.CreditCard)
                      .WithMany()
                      .HasForeignKey(so => so.CreditCardId)
                      .OnDelete(DeleteBehavior.NoAction);
            });
        }
    }
}
