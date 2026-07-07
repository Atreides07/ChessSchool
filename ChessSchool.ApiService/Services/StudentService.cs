using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Services;

/// <summary>
/// Домен личного кабинета школы: листинги учеников/очереди атрибуции, профиль ученика, создание,
/// атрибуция партий, привязка к онлайн-аккаунту и шаринг профиля родителю. Вынесено из Program.cs,
/// чтобы эндпоинты были тонкими (request→service→response), а доменные запросы/события — здесь.
/// </summary>
public sealed class StudentService(
    SchoolDbContext db, GameArchiver archiver, IdpUserClient idp, IAnalytics analytics)
{
    private static StudentDto ToDto(Student s) =>
        new(s.Id, s.GroupId, s.DisplayName, s.Rating, s.RatingDeviation, s.GamesPlayed, s.Wins, s.Draws, s.Losses, s.LinkedUserSub, s.BirthDate);

    // Пагинация: единый разбор и ограничение страницы (защита от выборки «всё» на больших таблицах).
    private static (int Skip, int Take) Page(int? skip, int? take, int maxTake = 200, int defaultTake = 100) =>
        (Math.Max(0, skip ?? 0), Math.Clamp(take ?? defaultTake, 1, maxTake));

    public async Task<IReadOnlyList<StudentDto>> ListBySchoolAsync(Guid schoolId, int? skip, int? take, CancellationToken ct)
    {
        var (s, t) = Page(skip, take);
        // Один запрос с join, проекцией в DTO и пагинацией — без трекинга и без выборки всей таблицы.
        var list = await (
            from st in db.Students.AsNoTracking()
            join g in db.Groups on st.GroupId equals g.Id
            where g.SchoolId == schoolId
            orderby st.Rating descending
            select new StudentDto(st.Id, st.GroupId, st.DisplayName, st.Rating, st.RatingDeviation,
                st.GamesPlayed, st.Wins, st.Draws, st.Losses, st.LinkedUserSub, st.BirthDate))
            .Skip(s).Take(t).ToListAsync(ct);

        // Тренд рейтинга за 7 дней: current − (последняя точка истории на момент ≤ неделю назад).
        // Отдельным запросом по показанной странице, чтобы не тащить всю историю в основной проекции.
        var ids = list.Select(x => x.Id).ToList();
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        var baseline = (await db.RatingPoints.AsNoTracking()
                .Where(r => ids.Contains(r.StudentId) && r.Date <= weekAgo)
                .Select(r => new { r.StudentId, r.Date, r.Rating }).ToListAsync(ct))
            .GroupBy(r => r.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Date).First().Rating);

        return list.Select(x => baseline.TryGetValue(x.Id, out var b) ? x with { RecentDelta = x.Rating - b } : x).ToList();
    }

    public async Task<IReadOnlyList<PendingGameDto>> ListPendingGamesAsync(Guid schoolId, int? skip, int? take, CancellationToken ct)
    {
        var (s, t) = Page(skip, take);
        return await db.Games.AsNoTracking()
            .Where(g => g.Source == AttributionSource.None && g.WhiteStudentId == null)
            .OrderByDescending(g => g.PlayedAt)
            .Skip(s).Take(t)
            .Select(g => new PendingGameDto(g.Id, g.PlayedAt, g.DeviceRef ?? "—", g.Pgn))
            .ToListAsync(ct);
    }

    /// <summary>Сводка для дашборда тренера за неделю: рост/падение рейтинга, неактивные, активность.
    /// Холодный путь с малыми данными (одна школа) — считаем в памяти по ограниченным окнам выборок.</summary>
    public async Task<SchoolInsightsDto> GetInsightsAsync(Guid schoolId, CancellationToken ct)
    {
        const int inactiveDays = 14;
        var now = DateTimeOffset.UtcNow;
        var weekAgo = now.AddDays(-7);

        var students = await (
            from st in db.Students.AsNoTracking()
            join g in db.Groups on st.GroupId equals g.Id
            where g.SchoolId == schoolId
            select new { st.Id, st.DisplayName, st.Rating }).ToListAsync(ct);
        if (students.Count == 0) return new SchoolInsightsDto([], [], [], 0, 0, 0);
        var ids = students.Select(s => s.Id).ToList();

        // База рейтинга неделю назад: последняя точка истории на момент ≤ weekAgo (по каждому ученику).
        var baseline = (await db.RatingPoints.AsNoTracking()
                .Where(r => ids.Contains(r.StudentId) && r.Date <= weekAgo)
                .Select(r => new { r.StudentId, r.Date, r.Rating }).ToListAsync(ct))
            .GroupBy(r => r.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Date).First().Rating);

        // Партии учеников: дата последней (активность) и счётчик за неделю.
        var games = await db.Games.AsNoTracking()
            .Where(g => (g.WhiteStudentId != null && ids.Contains(g.WhiteStudentId.Value))
                     || (g.BlackStudentId != null && ids.Contains(g.BlackStudentId.Value)))
            .Select(g => new { g.WhiteStudentId, g.BlackStudentId, g.PlayedAt }).ToListAsync(ct);

        var lastGame = new Dictionary<Guid, DateTimeOffset>();
        var playedThisWeek = new HashSet<Guid>();
        foreach (var g in games)
            foreach (var sid in new[] { g.WhiteStudentId, g.BlackStudentId })
                if (sid is { } id && ids.Contains(id))
                {
                    if (!lastGame.TryGetValue(id, out var d) || g.PlayedAt > d) lastGame[id] = g.PlayedAt;
                    if (g.PlayedAt >= weekAgo) playedThisWeek.Add(id);
                }

        var deltas = students
            .Where(s => baseline.ContainsKey(s.Id))
            .Select(s => new InsightStudentDto(s.Id, s.DisplayName, s.Rating - baseline[s.Id]))
            .ToList();

        var improved = deltas.Where(d => d.Delta > 0).OrderByDescending(d => d.Delta).Take(5).ToList();
        var declined = deltas.Where(d => d.Delta < 0).OrderBy(d => d.Delta).Take(5).ToList();

        var inactive = students
            .Select(s => new InactiveStudentDto(s.Id, s.DisplayName,
                lastGame.TryGetValue(s.Id, out var d) ? (int)(now - d).TotalDays : null))
            .Where(x => x.DaysSinceLastGame is null || x.DaysSinceLastGame >= inactiveDays)
            .OrderByDescending(x => x.DaysSinceLastGame ?? int.MaxValue)
            .Take(5).ToList();

        return new SchoolInsightsDto(improved, declined, inactive,
            playedThisWeek.Count, games.Count(g => g.PlayedAt >= weekAgo), students.Count);
    }

    public async Task<StudentProfileDto?> GetProfileAsync(Guid studentId, CancellationToken ct)
    {
        var student = await db.Students.FindAsync([studentId], ct);
        if (student is null) return null;

        var history = await db.RatingPoints.AsNoTracking()
            .Where(r => r.StudentId == studentId)
            .OrderBy(r => r.Date)
            .Select(r => new RatingPointDto(r.Date, r.Rating))
            .ToListAsync(ct);

        // Только нужные колонки последних 10 партий (без трекинга и лишних полей).
        var games = await db.Games.AsNoTracking()
            .Where(g => g.WhiteStudentId == studentId || g.BlackStudentId == studentId)
            .OrderByDescending(g => g.PlayedAt).Take(10)
            .Select(g => new
            {
                g.Id,
                g.PlayedAt,
                g.WhiteStudentId,
                g.BlackStudentId,
                g.Result,
                g.WhiteRatingChange,
                g.BlackRatingChange,
                g.Pgn
            })
            .ToListAsync(ct);

        // Имена соперников — только по фактически встретившимся id (а не вся таблица учеников).
        var oppIds = games
            .Select(g => g.WhiteStudentId == studentId ? g.BlackStudentId : g.WhiteStudentId)
            .OfType<Guid>().Distinct().ToList();
        var names = await db.Students.AsNoTracking()
            .Where(s => oppIds.Contains(s.Id))
            .Select(s => new { s.Id, s.DisplayName })
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        var summaries = games.Select(g =>
        {
            bool isWhite = g.WhiteStudentId == studentId;
            var oppId = isWhite ? g.BlackStudentId : g.WhiteStudentId;
            var oppName = oppId is { } id && names.TryGetValue(id, out var n) ? n : "Гость";
            return new GameSummaryDto(g.Id, g.PlayedAt, oppName,
                isWhite ? PieceColor.White : PieceColor.Black, g.Result,
                isWhite ? g.WhiteRatingChange : g.BlackRatingChange, g.Pgn);
        }).ToList();

        return new StudentProfileDto(ToDto(student), history, summaries);
    }

    /// <summary>Создать ученика. Возвращает DTO либо текст ошибки (группа не в этой школе).</summary>
    public async Task<(StudentDto? Dto, string? Error)> CreateAsync(Guid schoolId, CreateStudentRequest req, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == req.GroupId && g.SchoolId == schoolId, ct))
            return (null, "Группа не найдена в этой школе.");

        var student = new Student { GroupId = req.GroupId, DisplayName = req.DisplayName, BirthDate = req.BirthDate };
        db.Students.Add(student);
        await db.SaveChangesAsync(ct);
        analytics.Capture("student_created", schoolId.ToString(), new Dictionary<string, object?> { ["group_id"] = req.GroupId });
        return (ToDto(student), null);
    }

    /// <summary>Отредактировать ученика (имя/дата рождения). null — ученик не найден.</summary>
    public async Task<StudentDto?> UpdateAsync(Guid studentId, UpdateStudentRequest req, CancellationToken ct)
    {
        var student = await db.Students.FindAsync([studentId], ct);
        if (student is null) return null;
        student.DisplayName = req.DisplayName;
        student.BirthDate = req.BirthDate;
        await db.SaveChangesAsync(ct);
        analytics.Capture("student_updated", studentId.ToString());
        return ToDto(student);
    }

    /// <summary>Группы школы (по алфавиту).</summary>
    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(Guid schoolId, CancellationToken ct) =>
        await db.Groups.AsNoTracking().Where(g => g.SchoolId == schoolId).OrderBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.SchoolId, g.Name)).ToListAsync(ct);

    /// <summary>Создать группу в школе.</summary>
    public async Task<GroupDto> CreateGroupAsync(Guid schoolId, string name, CancellationToken ct)
    {
        var group = new Group { SchoolId = schoolId, Name = name };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        analytics.Capture("group_created", schoolId.ToString());
        return new GroupDto(group.Id, group.SchoolId, group.Name);
    }

    public enum MoveOutcome { Ok, StudentNotFound, GroupNotInSchool }

    /// <summary>Перевести ученика в другую группу — только в пределах его же школы (не даёт увести в чужую).</summary>
    public async Task<MoveOutcome> MoveStudentAsync(Guid studentId, Guid targetGroupId, CancellationToken ct)
    {
        var student = await db.Students.FindAsync([studentId], ct);
        if (student is null) return MoveOutcome.StudentNotFound;
        var currentSchool = await db.Groups.Where(g => g.Id == student.GroupId).Select(g => (Guid?)g.SchoolId).FirstOrDefaultAsync(ct);
        var targetSchool = await db.Groups.Where(g => g.Id == targetGroupId).Select(g => (Guid?)g.SchoolId).FirstOrDefaultAsync(ct);
        if (targetSchool is null || targetSchool != currentSchool) return MoveOutcome.GroupNotInSchool;
        student.GroupId = targetGroupId;
        await db.SaveChangesAsync(ct);
        analytics.Capture("student_moved", studentId.ToString(), new Dictionary<string, object?> { ["group_id"] = targetGroupId });
        return MoveOutcome.Ok;
    }

    public enum AttributeOutcome { Ok, GameNotFound, StudentNotFound }

    /// <summary>Привязать партию из очереди к ученикам и пересчитать рейтинг (через GameArchiver).</summary>
    public async Task<AttributeOutcome> AttributeAsync(Guid gameId, AttributeGameRequest req, CancellationToken ct)
    {
        var game = await db.Games.FindAsync([gameId], ct);
        if (game is null) return AttributeOutcome.GameNotFound;
        var white = await db.Students.FindAsync([req.WhiteStudentId], ct);
        var black = await db.Students.FindAsync([req.BlackStudentId], ct);
        if (white is null || black is null) return AttributeOutcome.StudentNotFound;

        await archiver.AttributeAsync(game, white, black, req.Result, ct);
        analytics.Capture("game_attributed", gameId.ToString(), new Dictionary<string, object?> { ["result"] = req.Result.ToString() });
        return AttributeOutcome.Ok;
    }

    public enum LinkOutcome { Ok, StudentNotFound, UserNotFound }

    /// <summary>Привязать ученика к онлайн-аккаунту по e-mail (резолв sub в IdP).</summary>
    public async Task<(LinkOutcome Outcome, StudentDto? Dto)> LinkAsync(Guid studentId, string email, CancellationToken ct)
    {
        var student = await db.Students.FindAsync([studentId], ct);
        if (student is null) return (LinkOutcome.StudentNotFound, null);

        var found = await idp.ResolveByEmailAsync(email, ct);
        if (found is null) return (LinkOutcome.UserNotFound, null);

        student.LinkedUserSub = found.Sub;
        await db.SaveChangesAsync(ct);
        analytics.Capture("student_account_linked", found.Sub, new Dictionary<string, object?> { ["student_id"] = studentId });
        return (LinkOutcome.Ok, ToDto(student));
    }

    /// <summary>Создать ссылку-шаринг профиля родителю (90 дней). null — ученик не найден.</summary>
    public async Task<ShareLinkDto?> CreateShareAsync(Guid studentId, CancellationToken ct)
    {
        if (!await db.Students.AnyAsync(s => s.Id == studentId, ct)) return null;
        var token = Guid.NewGuid().ToString("N");
        var expires = DateTimeOffset.UtcNow.AddDays(90);
        db.ShareLinks.Add(new ShareLink { StudentId = studentId, Token = token, ExpiresAt = expires });
        await db.SaveChangesAsync(ct);
        analytics.Capture("share_link_created", studentId.ToString());
        return new ShareLinkDto(token, $"/p/{token}", expires);
    }

    /// <summary>Все ссылки-шаринги ученика (для управления в ЛК) — от свежих к старым.</summary>
    public async Task<IReadOnlyList<ShareLinkInfoDto>> ListSharesAsync(Guid studentId, CancellationToken ct) =>
        await db.ShareLinks.AsNoTracking()
            .Where(l => l.StudentId == studentId)
            .OrderByDescending(l => l.ExpiresAt)
            .Select(l => new ShareLinkInfoDto(l.Token, $"/p/{l.Token}", l.ExpiresAt, l.Revoked))
            .ToListAsync(ct);

    /// <summary>Отозвать ссылку родителю (по токену, в пределах ученика). true — отозвана/уже была отозвана.</summary>
    public async Task<bool> RevokeShareAsync(Guid studentId, string token, CancellationToken ct)
    {
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.StudentId == studentId && l.Token == token, ct);
        if (link is null) return false;
        if (!link.Revoked) { link.Revoked = true; await db.SaveChangesAsync(ct); }
        analytics.Capture("share_link_revoked", studentId.ToString());
        return true;
    }

    /// <summary>Профиль по ссылке-шарингу (для родителя). null — ссылка не найдена/просрочена/отозвана.</summary>
    public async Task<StudentProfileDto?> GetSharedProfileAsync(string token, CancellationToken ct)
    {
        var link = await db.ShareLinks.FirstOrDefaultAsync(s => s.Token == token && !s.Revoked, ct);
        if (link is null || (link.ExpiresAt is { } e && e < DateTimeOffset.UtcNow)) return null;
        if (await GetProfileAsync(link.StudentId, ct) is not { } p) return null;
        analytics.Capture("parent_profile_viewed", link.StudentId.ToString(), new Dictionary<string, object?> { ["source"] = "share_link" });
        return p;
    }
}
