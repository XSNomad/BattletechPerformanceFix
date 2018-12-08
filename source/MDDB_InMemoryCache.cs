﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Harmony;
using BattleTech;
using BattleTech.Data;
using HBS.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using BattleTech.Rendering;
using UnityEngine;
using System.Data;
using System.Data.SQLite;
using Dapper;

namespace BattletechPerformanceFix
{
    // source = sqlite3.connect('existing_db.db')
    // dest = sqlite3.connect(':memory:')
    // source.backup(dest)
    public class MDDB_InMemoryCache : Feature
    {
        public void Activate()
        {
            var modtek = Control.ModTekType;
            Control.Log("Found modtek? {0}", modtek?.FullName);

            if (modtek == null)
                throw new Exception("Aborting MDDB_InMEmoryCache patch: Could not find ModTek");

            var path = System.Environment.GetEnvironmentVariable("PATH");
            var editor_folder = Path.GetFullPath(Control.ModDir); // "./BattleTech_Data/StreamingAssets/editor");
            System.Environment.SetEnvironmentVariable("PATH", path + $";{editor_folder}");

            try { new SQLiteConnection("Data Source=:memory:"); }
            catch(Exception e) { Control.LogWarning("SQlite dependencies not found. Aborting MDDB patch.");
                                 Control.LogException(e);
                                 throw new Exception("MDDB Patch aborted (This is okay, you just won't get the performance improvement)"); }

            Control.Trap(() =>
            Control.harmony.Patch(AccessTools.Method(typeof(FileBackedSQLiteDB), "Open")
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), "Open")));
            Control.Trap(() =>
            Control.harmony.Patch(AccessTools.Method(typeof(FileBackedSQLiteDB), "Close")
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), "Close")));

            Control.Trap(() =>
            Control.harmony.Patch(AccessTools.Method(typeof(FileBackedSQLiteDB), "Close")
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), "Close")));

            Control.harmony.Patch(AccessTools.Method(typeof(MapsAndEncounters_MDDExtensions), "GetMapByPath")
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), "GetMapByPath"));

            

            Control.harmony.Patch(AccessTools.Method(modtek, "WriteJsonFile")
                                 , null
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), nameof(MDDB_InMemoryCache.SaveToDisk)));
            Control.harmony.Patch(AccessTools.Method(typeof(BattleTech.OnGameShutdown), "ShutdownFileIO")
                                 , null
                                 , new HarmonyMethod(typeof(MDDB_InMemoryCache), nameof(MDDB_InMemoryCache.SaveToDisk)));
        }

        public static bool Open(FileBackedSQLiteDB __instance, ref IDbConnection ___connection)
        {
            Control.Trap(() =>
            {
                if (ConnectionURI != null && ConnectionURI != __instance.ConnectionURI)
                {
                    Control.LogException("MDDB_InMemoryCache: Expected {0} but got {1}", ConnectionURI, __instance.ConnectionURI);
                }
                if (memoryStore == null)
                {

                    ConnectionURI = __instance.ConnectionURI;
                    Control.Log("MDDB_InMemoryCache Open {0} -> :memory:", ConnectionURI);
                    mstore = new SQLiteConnection("Data Source=:memory:");
                    mstore.Open();
                    var disk = new SQLiteConnection(__instance.ConnectionURI);
                    disk.Open();
                    disk.BackupDatabase(mstore, mstore.Database, disk.Database, -1, null, -1);
                    disk.Close();
                    
                    memoryStore = new SQLProxy(mstore);
                }
            });

            ___connection = memoryStore;

            return false;
        }

        public static void SaveToDisk()
        {
            Control.Trap(() =>
            {
                if (ConnectionURI == null)
                {
                    Control.LogWarning("Tried to save MDDB but no connection info");
                    return;
                }
                
                Control.Log("MDDB_InMemoryCache Write :memory: {0}", ConnectionURI);
                var disk = new SQLiteConnection(ConnectionURI);
                disk.Open();
                
                mstore.BackupDatabase(disk, disk.Database, mstore.Database, -1, null, -1);
                disk.Close();
            });
        }

        public static bool Close()
        {
            return false;
        }

        static SQLiteConnection mstore = null;
        static IDbConnection memoryStore = null;
        static string ConnectionURI = null;
    }

    class SQLProxy : IDbConnection
    {
        SQLiteConnection conn;
        public SQLProxy(SQLiteConnection conn)
        {
            this.conn = conn;
        }
        public IDbTransaction BeginTransaction() => conn.BeginTransaction();
        public IDbTransaction BeginTransaction(IsolationLevel il) => conn.BeginTransaction(il);
        public void ChangeDatabase(string databaseName) => conn.ChangeDatabase(databaseName);
        public void Close() { Control.Log("EFXR CLOSE"); conn.Close(); }
        public IDbCommand CreateCommand() => conn.CreateCommand();
        public void Open() { Control.Log("EFXR OPEN"); conn.Open(); }
        public string ConnectionString { get => conn.ConnectionString; set => conn.ConnectionString = value; }
        public int ConnectionTimeout { get => conn.ConnectionTimeout; }
        public string Database { get => conn.Database; }
        public ConnectionState State { get => conn.State; }
        public void Dispose() { Control.Log("EFXR DISPOSE"); conn.Dispose(); }
}
}

