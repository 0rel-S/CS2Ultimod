using System;
using Microsoft.Data.Sqlite;

var dbPath = args.Length > 0 ? args[0] : "plugin.db";
using var db = new SqliteConnection("Data Source=" + dbPath);
db.Open();

void Exec(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

Exec("PRAGMA journal_mode=WAL");
Exec("CREATE TABLE IF NOT EXISTS core_migrations (id TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL DEFAULT (datetime('now')))");
Exec("CREATE TABLE IF NOT EXISTS admin_admins (steam_id TEXT NOT NULL, name TEXT NOT NULL DEFAULT '', flag TEXT NOT NULL, immunity INTEGER NOT NULL DEFAULT 0, expires_at TEXT, PRIMARY KEY(steam_id, flag))");
Exec("CREATE TABLE IF NOT EXISTS admin_bans (id INTEGER PRIMARY KEY AUTOINCREMENT, steam_id TEXT NOT NULL, admin_id TEXT NOT NULL DEFAULT 'CONSOLE', reason TEXT NOT NULL DEFAULT '', duration INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now')), expires_at TEXT)");
Exec("CREATE TABLE IF NOT EXISTS admin_comms (id INTEGER PRIMARY KEY AUTOINCREMENT, steam_id TEXT NOT NULL, admin_id TEXT NOT NULL DEFAULT 'CONSOLE', type TEXT NOT NULL DEFAULT 'gag', reason TEXT NOT NULL DEFAULT '', duration INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now')), expires_at TEXT)");
Exec("INSERT OR REPLACE INTO core_migrations (id, version) VALUES ('admin_v1_init', 100)");
Exec("INSERT OR REPLACE INTO admin_admins (steam_id, name, flag, immunity) VALUES ('76561198007698872', '0rel', '@css/root', 100)");

Console.WriteLine("DB crÃ©Ã©e : " + dbPath);
Console.WriteLine("Admin : 0rel (76561198007698872) â€” @css/root");


