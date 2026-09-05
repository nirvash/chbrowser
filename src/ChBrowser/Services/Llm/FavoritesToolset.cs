using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Llm;

/// <summary>MCP / AI チャットからお気に入りツリーを管理するツールセット。</summary>
public sealed class FavoritesToolset : IAgentToolset
{
    private readonly FavoritesViewModel _favorites;
    private readonly Action _refreshFavoritedState;

    public FavoritesToolset(FavoritesViewModel favorites, Action refreshFavoritedState)
    {
        _favorites = favorites;
        _refreshFavoritedState = refreshFavoritedState;
    }

    public IReadOnlyList<object> GetToolDefinitions() => new object[]
    {
        Def("list_favorites", "お気に入りツリーを取得する。変更・移動の前に id をここから確認する。", new { type = "object", properties = new { } }),
        Def("create_favorite_folder", "お気に入りフォルダを作成する。parent_id を省略するとルートに追加する。", new { type = "object", properties = new { name = new { type = "string" }, parent_id = new { type = "string" } }, required = new[] { "name" } }),
        Def("add_favorite_board", "板をお気に入りへ追加する。既に同じ host と directory_name の板があれば追加しない。", new { type = "object", properties = new { host = new { type = "string" }, directory_name = new { type = "string" }, board_name = new { type = "string" }, parent_id = new { type = "string" } }, required = new[] { "host", "directory_name" } }),
        Def("add_favorite_thread", "スレッドをお気に入りへ追加する。既に同じ host、directory_name、thread_key のスレッドがあれば追加しない。", new { type = "object", properties = new { host = new { type = "string" }, directory_name = new { type = "string" }, thread_key = new { type = "string" }, title = new { type = "string" }, board_name = new { type = "string" }, parent_id = new { type = "string" } }, required = new[] { "host", "directory_name", "thread_key", "title" } }),
        Def("delete_favorite", "指定したお気に入り項目を削除する。フォルダの場合は子もすべて削除する。", new { type = "object", properties = new { favorite_id = new { type = "string" } }, required = new[] { "favorite_id" } }),
        Def("move_favorite", "お気に入り項目を移動する。inside は target_id のフォルダ末尾、before/after は target_id の兄弟、root_end はルート末尾。", new { type = "object", properties = new { favorite_id = new { type = "string" }, position = new { type = "string", @enum = new[] { "inside", "before", "after", "root_end" } }, target_id = new { type = "string" } }, required = new[] { "favorite_id", "position" } }),
        Def("rename_favorite_folder", "お気に入りフォルダ名を変更する。", new { type = "object", properties = new { favorite_id = new { type = "string" }, name = new { type = "string" } }, required = new[] { "favorite_id", "name" } }),
    };

    public Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Task.FromResult(Error("引数は JSON オブジェクトで指定してください"));
            var args = doc.RootElement;
            return Task.FromResult(name switch
            {
                "list_favorites" => Json(new { favorites = _favorites.Items.Select(Entry).ToArray() }),
                "create_favorite_folder" => CreateFolder(args),
                "add_favorite_board" => AddBoard(args),
                "add_favorite_thread" => AddThread(args),
                "delete_favorite" => Delete(args),
                "move_favorite" => Move(args),
                "rename_favorite_folder" => RenameFolder(args),
                _ => Error($"未知のツール: {name}"),
            });
        }
        catch (JsonException) { return Task.FromResult(Error("引数 JSON のパースに失敗しました")); }
        catch (Exception ex) { return Task.FromResult(Error($"お気に入り操作で例外: {ex.Message}")); }
    }

    private string CreateFolder(JsonElement args)
    {
        var name = Required(args, "name");
        if (string.IsNullOrWhiteSpace(name)) return Error("name は空でない文字列で指定してください");
        var parent = Parent(args); if (parent.error is not null) return Error(parent.error);
        var entry = new FavoriteFolder { Name = name.Trim() };
        if (parent.folder is null) _favorites.AddRoot(entry); else _favorites.AddInto(parent.folder, entry);
        Changed(); return Json(new { created = true, favorite = Entry(_favorites.FindById(entry.Id)!) });
    }

    private string AddBoard(JsonElement args)
    {
        var host = Required(args, "host"); var directory = Required(args, "directory_name");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(directory)) return Error("host と directory_name は空でない文字列で指定してください");
        if (_favorites.FindBoard(host, directory) is not null) return Error("同じ板は既にお気に入りにあります");
        var parent = Parent(args); if (parent.error is not null) return Error(parent.error);
        var entry = new FavoriteBoard { Host = host, DirectoryName = directory, BoardName = Optional(args, "board_name") ?? directory };
        if (parent.folder is null) _favorites.AddRoot(entry); else _favorites.AddInto(parent.folder, entry);
        Changed(); return Json(new { created = true, favorite = Entry(_favorites.FindById(entry.Id)!) });
    }

    private string AddThread(JsonElement args)
    {
        var host = Required(args, "host"); var directory = Required(args, "directory_name"); var key = Required(args, "thread_key"); var title = Required(args, "title");
        if (new[] { host, directory, key, title }.Any(string.IsNullOrWhiteSpace)) return Error("host、directory_name、thread_key、title は空でない文字列で指定してください");
        if (_favorites.FindThread(host!, directory!, key!) is not null) return Error("同じスレッドは既にお気に入りにあります");
        var parent = Parent(args); if (parent.error is not null) return Error(parent.error);
        var entry = new FavoriteThread { Host = host!, DirectoryName = directory!, ThreadKey = key!, Title = title!, BoardName = Optional(args, "board_name") ?? directory! };
        if (parent.folder is null) _favorites.AddRoot(entry); else _favorites.AddInto(parent.folder, entry);
        Changed(); return Json(new { created = true, favorite = Entry(_favorites.FindById(entry.Id)!) });
    }

    private string Delete(JsonElement args)
    {
        var entry = Find(args, "favorite_id"); if (entry.error is not null) return Error(entry.error);
        _favorites.Remove(entry.value!); Changed(); return Json(new { deleted = true, favorite_id = entry.value!.Model.Id });
    }

    private string Move(JsonElement args)
    {
        var source = Find(args, "favorite_id"); if (source.error is not null) return Error(source.error);
        var position = Required(args, "position");
        if (position == "root_end") { _favorites.MoveToRootEnd(source.value!); Changed(); return Json(new { moved = true, favorite = Entry(source.value!) }); }
        if (position is not ("inside" or "before" or "after")) return Error("position は inside、before、after、root_end のいずれかです");
        var target = Find(args, "target_id"); if (target.error is not null) return Error(target.error);
        if (ReferenceEquals(source.value, target.value)) return Error("移動元と移動先を同じ項目にはできません");
        if (position == "inside")
        {
            if (target.value is not FavoriteFolderViewModel folder) return Error("inside の target_id はフォルダである必要があります");
            if (!_favorites.CanReparent(source.value!, folder)) return Error("フォルダ自身または子孫の中には移動できません");
            _favorites.MoveIntoFolder(source.value!, folder);
        }
        else
        {
            if (!_favorites.CanReparent(source.value!, target.value!.Parent)) return Error("子孫と同じ階層には移動できません");
            if (position == "before") _favorites.MoveAsSiblingBefore(source.value!, target.value!); else _favorites.MoveAsSiblingAfter(source.value!, target.value!);
        }
        Changed(); return Json(new { moved = true, favorite = Entry(source.value!) });
    }

    private string RenameFolder(JsonElement args)
    {
        var entry = Find(args, "favorite_id"); var name = Required(args, "name");
        if (entry.error is not null) return Error(entry.error);
        if (entry.value is not FavoriteFolderViewModel folder) return Error("リネームできるのはフォルダだけです");
        if (string.IsNullOrWhiteSpace(name)) return Error("name は空でない文字列で指定してください");
        _favorites.RenameFolder(folder, name); Changed(); return Json(new { renamed = true, favorite = Entry(folder) });
    }

    private (FavoriteFolderViewModel? folder, string? error) Parent(JsonElement args)
    {
        var id = Optional(args, "parent_id"); if (id is null) return (null, null);
        var found = FindById(id); if (found.error is not null) return (null, found.error);
        return found.value is FavoriteFolderViewModel folder ? (folder, null) : (null, "parent_id はフォルダの id である必要があります");
    }
    private (FavoriteEntryViewModel? value, string? error) Find(JsonElement args, string property)
    {
        var id = Required(args, property); return id is null ? (null, $"{property} が指定されていません") : FindById(id);
    }
    private (FavoriteEntryViewModel? value, string? error) FindById(string id)
    {
        if (!Guid.TryParse(id, out var guid)) return (null, "id は GUID 形式で指定してください");
        var entry = _favorites.FindById(guid); return entry is null ? (null, "指定されたお気に入り項目は存在しません") : (entry, null);
    }
    private void Changed() => _refreshFavoritedState();
    private static string? Required(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Optional(JsonElement args, string name) => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static object Entry(FavoriteEntryViewModel entry) => entry switch
    {
        FavoriteFolderViewModel folder => new { id = folder.Model.Id, type = "folder", name = folder.Name, children = folder.Children.Select(Entry).ToArray() },
        FavoriteBoardViewModel board => new { id = board.Model.Id, type = "board", name = board.Model.BoardName, host = board.Model.Host, directory_name = board.Model.DirectoryName },
        FavoriteThreadViewModel thread => new { id = thread.Model.Id, type = "thread", name = thread.Model.Title, host = thread.Model.Host, directory_name = thread.Model.DirectoryName, thread_key = thread.Model.ThreadKey, board_name = thread.Model.BoardName },
        _ => throw new InvalidOperationException("未知のお気に入り項目です"),
    };
    private static object Def(string name, string description, object parameters) => new { type = "function", function = new { name, description, parameters } };
    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Error(string message) => Json(new { error = message });
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
