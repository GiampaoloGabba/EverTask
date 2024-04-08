﻿// <auto-generated />
using System;
using EverTask.Storage.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace EverTask.Storage.SqlServer.Migrations
{
    [DbContext(typeof(SqlServerTaskStoreContext))]
    partial class TaskStoreDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasDefaultSchema("EverTask")
                .HasAnnotation("ProductVersion", "7.0.14")
                .HasAnnotation("Relational:MaxIdentifierLength", 128);

            SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

            modelBuilder.Entity("EverTask.Storage.QueuedTask", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTimeOffset>("CreatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<int?>("CurrentRunCount")
                        .HasColumnType("int");

                    b.Property<string>("Exception")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("Handler")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("nvarchar(500)");

                    b.Property<bool>("IsRecurring")
                        .HasColumnType("bit");

                    b.Property<DateTimeOffset?>("LastExecutionUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<int?>("MaxRuns")
                        .HasColumnType("int");

                    b.Property<DateTimeOffset?>("NextRunUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("RecurringInfo")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("RecurringTask")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("Request")
                        .IsRequired()
                        .HasColumnType("nvarchar(max)");

                    b.Property<DateTimeOffset?>("RunUntil")
                        .HasColumnType("datetimeoffset");

                    b.Property<DateTimeOffset?>("ScheduledExecutionUtc")
                        .HasColumnType("datetimeoffset");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(15)
                        .HasColumnType("nvarchar(15)");

                    b.Property<string>("Type")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("nvarchar(500)");

                    b.HasKey("Id");

                    b.HasIndex("Status");

                    b.ToTable("QueuedTasks", "EverTask");
                });

            modelBuilder.Entity("EverTask.Storage.RunsAudit", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));

                    b.Property<string>("Exception")
                        .HasColumnType("nvarchar(max)");

                    b.Property<DateTimeOffset>("ExecutedAt")
                        .HasColumnType("datetimeoffset");

                    b.Property<Guid>("QueuedTaskId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(15)
                        .HasColumnType("nvarchar(15)");

                    b.HasKey("Id");

                    b.HasIndex("QueuedTaskId");

                    b.ToTable("RunsAudit", "EverTask");
                });

            modelBuilder.Entity("EverTask.Storage.StatusAudit", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("bigint");

                    SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));

                    b.Property<string>("Exception")
                        .HasColumnType("nvarchar(max)");

                    b.Property<string>("NewStatus")
                        .IsRequired()
                        .HasMaxLength(15)
                        .HasColumnType("nvarchar(15)");

                    b.Property<Guid>("QueuedTaskId")
                        .HasColumnType("uniqueidentifier");

                    b.Property<DateTimeOffset>("UpdatedAtUtc")
                        .HasColumnType("datetimeoffset");

                    b.HasKey("Id");

                    b.HasIndex("QueuedTaskId");

                    b.ToTable("StatusAudit", "EverTask");
                });

            modelBuilder.Entity("EverTask.Storage.RunsAudit", b =>
                {
                    b.HasOne("EverTask.Storage.QueuedTask", "QueuedTask")
                        .WithMany("RunsAudits")
                        .HasForeignKey("QueuedTaskId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("QueuedTask");
                });

            modelBuilder.Entity("EverTask.Storage.StatusAudit", b =>
                {
                    b.HasOne("EverTask.Storage.QueuedTask", "QueuedTask")
                        .WithMany("StatusAudits")
                        .HasForeignKey("QueuedTaskId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("QueuedTask");
                });

            modelBuilder.Entity("EverTask.Storage.QueuedTask", b =>
                {
                    b.Navigation("RunsAudits");

                    b.Navigation("StatusAudits");
                });
#pragma warning restore 612, 618
        }
    }
}
