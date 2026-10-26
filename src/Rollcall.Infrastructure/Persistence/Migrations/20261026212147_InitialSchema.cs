// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rollcall.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "worker_heartbeat",
                columns: table => new
                {
                    worker_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    beat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_heartbeat", x => x.worker_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_heartbeat");
        }
    }
}
