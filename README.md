# SQLite data provider for nopCommerce

Runs a nopCommerce store on a single local SQLite database file, with no database server to install.

It is a plain plugin: the nopCommerce source (Nop.Core, Nop.Data, Nop.Services, Nop.Web) is not changed. Stores running on SQL Server, MySQL or PostgreSQL are not affected, even with the plugin installed.

## Branches

Each nopCommerce version has its own branch:

| Branch | nopCommerce |
| --- | --- |
| `4.80` | 4.80 |

## Add it to your nopCommerce source

The plugin builds inside the nopCommerce source tree, next to the other plugins in `src/Plugins`. It references `Nop.Web` and `Nop.Web.Framework`, and uses nopCommerce's `ClearPluginAssemblies` build step. From the root of the nopCommerce repository (the folder that contains `src`):

```
git submodule add -b 4.80 https://github.com/dotnetdreamer/Nop.Plugin.Data.Sqlite.git src/Plugins/Nop.Plugin.Data.Sqlite
dotnet sln src/NopCommerce.sln add --solution-folder Plugins src/Plugins/Nop.Plugin.Data.Sqlite/Nop.Plugin.Data.Sqlite.csproj
```

You can also clone or copy it into `src/Plugins/Nop.Plugin.Data.Sqlite`.

Keep the store's database files out of git by adding these lines to your `.gitignore`:

```
src/Presentation/Nop.Web/App_Data/*.sqlite
src/Presentation/Nop.Web/App_Data/*.sqlite-wal
src/Presentation/Nop.Web/App_Data/*.sqlite-shm
```

## New store on SQLite

1. Build the solution. The plugin is deployed to `Presentation/Nop.Web/Plugins/Data.Sqlite`.
2. **Once, before the first start**, create `Presentation/Nop.Web/App_Data/plugins.json`. Before installation nopCommerce loads no plugins, so this file tells it to load this one for the installation wizard:

   ```json
   {
     "InstalledPlugins": [],
     "PluginNamesToUninstall": [],
     "PluginNamesToDelete": [],
     "PluginNamesToInstall": [ { "Item1": "Data.Sqlite", "Item2": null } ]
   }
   ```

3. Start the site, choose **SQLite** in the wizard, enter a database name and install. The server, user and password fields are ignored, and the database is created as `App_Data/{database name}.sqlite`. With *Enter raw connection string* checked, any Microsoft.Data.Sqlite connection string works, for example `Data Source=App_Data/nopcommerce.sqlite`.

The wizard installs this plugin together with the others.

## Configuration

`App_Data/appsettings.json` of a store running on SQLite:

```json
"ConnectionStrings": {
  "ConnectionString": "Data Source=App_Data/nopcommerce.sqlite",
  "DataProvider": 100
}
```

`100` stands for SQLite. nopCommerce's `DataProviderType` enum only names its built-in providers (1-3), so the plugin uses a number outside of them. *Admin > Configuration > App settings* shows it as "SQLite".

`Data Source` can be:

- relative, resolved against the application root rather than the current directory
- prefixed with `~/`
- `|DataDirectory|`, which means `App_Data`
- absolute

Any other [Microsoft.Data.Sqlite keyword](https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings) works as usual.

To start over:

1. Stop the site.
2. Delete the database files (`*.sqlite`, `-wal`, `-shm`), `App_Data/appsettings.json` and `App_Data/plugins.json`.
3. Repeat step 2 of *New store on SQLite*.

## How it works

| Concern | What the plugin does |
| --- | --- |
| Plugging in | Only dependency injection. It registers `SqliteDataProviderManager` for `IDataProviderManager` (which supplies `INopDataProvider`) and `SqliteProcessorAccessor` for the migration processor accessor, plus FluentMigrator's SQLite services. On another database it registers nothing. |
| Startup migrations | `NopDbStartup` (order 10) applies the schema migrations while it registers its services, using the built-in processors only. `SqliteEarlyStartup` (order 5) hides the database from it. `SqliteDataProviderStartup` (order 11) restores it and applies the migrations with the SQLite processor. |
| Installation wizard | `SqliteInstallationLocalizationService` adds SQLite to the provider list. `InstallController` creates data providers with a switch over the built-in ones, so `SqliteInstallationFilter` runs the installation when SQLite is posted. Apart from the provider it does what `InstallController.Index` does. |
| Concurrency | WAL journal mode: reads never block and are never blocked. All writes go through one background writer per database (`SqliteWriteQueue`) and run in arrival order on a single connection, so concurrent requests never fight for SQLite's single write lock ("database is locked"). Callers await their write as before. |
| Case-insensitive text | Text columns are created with `COLLATE NOCASE`, so `Email = 'ADMIN@X.COM'`, sorting and unique indexes behave like SQL Server's default collation (ASCII letters). |
| Foreign keys | Declared inside `CREATE TABLE` and enforced (`Foreign Keys=True`), so cascade deletes work as on SQL Server. |
| Identities | `AUTOINCREMENT`: identifiers are never reused, and bulk inserts continue from `sqlite_sequence`. |
| Migrations | Run in autocommit mode, because nopCommerce data migrations write through its services (other connections) while the migration runs. |
| Backup / restore | *Admin > System > Maintenance* works. Backups use the SQLite online backup API and produce a single `.bak` file. Restore runs inside the writer, after a `PRAGMA quick_check`. "Re-index" runs `REINDEX`, `ANALYZE` and `VACUUM`. |
| Native library | The plugin ships `e_sqlite3` for every platform in `runtimes/`. `SqliteNativeLibraryResolver` loads the right one, because nopCommerce deletes plugin `.deps.json` files. |
| Safety | The plugin refuses to be uninstalled while the store runs on SQLite. The *App settings* page keeps SQLite, because its list only offers the built-in providers. |

## Files

| Folder | Content |
| --- | --- |
| (root) | `SqliteNopDataProvider` and its connection, write queue, migration generator/processor and settings helpers; `SqlitePlugin` |
| `Infrastructure` | the three startups: `SqliteEarlyStartup` (5), `SqliteDataProviderStartup` (11), `SqliteInstallationStartup` (2100) |
| `Installation` | the installation wizard filter and provider list |
| `Filters` | the *App settings* filter |

## Porting to another nopCommerce version

Create a branch named after the version, starting from the closest existing branch. The plugin relies on these nopCommerce internals; check each one:

* `SqliteInstallationFilter` mirrors `InstallController.Index`.
* `NopDbStartup` applies the schema migrations in `ConfigureServices`. `SqliteDataProviderStartup.ApplySchemaMigrations` mirrors that code, and hiding the database uses `DataSettingsManager._databaseIsInstalled`.
* A startup with an order from 6 to 10, in this or another plugin, would see the database as not installed.
* If nopCommerce ever names SQLite in `DataProviderType`, switch to that value.
* Update `SupportedVersions` in `plugin.json` and the package versions (`Microsoft.Data.Sqlite` follows the .NET version of nopCommerce).

## Limits

* One machine: several processes may share the file, but not over a network share.
* No built-in encryption (`Password=` needs SQLCipher).
* Decimals are stored as 8-byte floating point, since SQLite has no decimal type. Prices and totals stay exact; rounding differences can appear only beyond 15 significant digits.
* `NOCASE` folds ASCII letters only (`É`/`é` compare as different).
* Some future nopCommerce upgrade migrations may not work on SQLite:
  * one that alters an existing column (`Alter.Column`) fails the upgrade
  * one that adds a foreign key to an existing table is skipped, with a warning in the log

## License

[GPL-3.0](LICENSE). The installation step (`SqliteInstallationFilter`) and the schema migration step (`SqliteDataProviderStartup.ApplySchemaMigrations`) are adapted from nopCommerce, which is licensed under the nopCommerce Public License (based on GPL-3.0).
