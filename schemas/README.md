# SQLite shard schema fixtures

The SQL files in this directory define the schema used to generate the local
SQLite shards exercised by the regression suite. They are included so that the
source tree can be built and tested without any external repository or private
build dependency.

The files are part of this source distribution and are licensed under
AGPL-3.0-or-later. See the repository `LICENSE` for the full terms.

- `schema.sql` - complete schema overview
- `schema-catalog.sql` - catalog shard tables
- `schema-launch.sql` - launch shard tables
- `schema-assets.sql` - assets shard tables
