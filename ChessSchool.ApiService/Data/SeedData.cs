using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Data;

/// <summary>Демо-данные для локального запуска: школа, группа, ученики с историей рейтинга и партиями.</summary>
public static class SeedData
{
    public static readonly Guid SchoolId = Demo.SchoolId;
    public static readonly Guid GroupId = Demo.GroupId;

    /// <summary>Демо-токен публичной страницы родителя (/p/{token}) — стабильный для удобства локального теста.</summary>
    public const string ParentShareToken = "demo-parent";

    /// <summary>
    /// Идемпотентный посев ПО ШАГАМ: восстанавливает недостающие части демо-данных, даже если часть уже
    /// есть (например строка школы уцелела в volume, а ученики пропали при пересоздании БД). Каждый шаг
    /// защищён гардом по фиксированным демо-ID, поэтому повторный запуск не плодит дубликаты. Ранний
    /// «return при db.Schools.Any()» намеренно убран — он и мешал репопуляции после сплита БД.
    /// </summary>
    public static void Ensure(SchoolDbContext db)
    {
        // 1. Школа (+ бэкфилл владельца для БД, созданных до OwnerSub — иначе демо-владелец получает 403).
        var school = db.Schools.FirstOrDefault(s => s.Id == SchoolId);
        if (school is null)
            db.Schools.Add(new School { Id = SchoolId, Name = "Шахматная школа №1", OwnerSub = Demo.OwnerSub });
        else if (school.OwnerSub is null)
            school.OwnerSub = Demo.OwnerSub;

        // 2. Группа.
        if (!db.Groups.Any(g => g.Id == GroupId))
            db.Groups.Add(new Group { Id = GroupId, SchoolId = SchoolId, Name = "Группа начинающих" });

        // 3. Устройства (доски idchess) школы — по одному на недостающий Ref.
        foreach (var deviceRef in new[] { "board-01", "board-02", "board-03" })
            if (!db.Devices.Any(d => d.SchoolId == SchoolId && d.Ref == deviceRef))
                db.Devices.Add(new Device { SchoolId = SchoolId, Ref = deviceRef });

        db.SaveChanges();

        // 4. Ученики с историей рейтинга — восстанавливаем, если в демо-группе их нет.
        if (!db.Students.Any(s => s.GroupId == GroupId))
        {
            AddSampleStudents(db, GroupId);
            db.SaveChanges();
        }

        // 5. Партии: пара сыгранных между учениками (влияют на рейтинг) + очередь атрибуции. Только если партий нет.
        if (!db.Games.Any())
        {
            SeedGames(db);
            db.SaveChanges();
        }

        // 6. Демо-ссылка родителю (публичная страница прогресса) — если ссылок ещё нет.
        if (!db.ShareLinks.Any())
        {
            var first = db.Students.Where(s => s.GroupId == GroupId).OrderBy(s => s.DisplayName).FirstOrDefault();
            if (first is not null)
            {
                db.ShareLinks.Add(new ShareLink { StudentId = first.Id, Token = ParentShareToken });
                db.SaveChanges();
            }
        }
    }

    /// <summary>
    /// Наполняет указанную группу примерными учениками с историей рейтинга (без SaveChanges — сохраняет
    /// вызывающий). Переиспользуется демо-посевом и провижинингом «моей школы» в Development, чтобы у любого
    /// dev-пользователя ЛК не был пустым (в проде новая школа остаётся пустой).
    /// </summary>
    public static void AddSampleStudents(SchoolDbContext db, Guid groupId)
    {
        // name, рейтинг, связанный IdP-sub (для онлайн-партий), дата рождения, W/D/L.
        var roster = new (string Name, int Rating, string? Sub, DateOnly Birth, int Wins, int Draws, int Losses)[]
        {
            ("Иван Петров",       1340, "demo-user-ivan",  new(2014, 3, 12), 6, 3, 3),
            ("Мария Сидорова",    1520, "demo-user-maria", new(2013, 7, 28), 9, 2, 2),
            ("Алексей Кузнецов",  1180, null,              new(2015, 1, 5),  3, 2, 7),
            ("Дарья Орлова",      1410, null,              new(2014, 11, 19), 7, 4, 3),
            ("Никита Волков",     1265, null,              new(2015, 5, 30), 5, 1, 6),
            ("София Морозова",    1600, "demo-user-sofia", new(2012, 9, 9),  11, 3, 1),
        };

        var baseDate = DateTimeOffset.UtcNow.AddDays(-60);
        foreach (var r in roster)
        {
            var student = new Student
            {
                GroupId = groupId,
                DisplayName = r.Name,
                Rating = r.Rating,
                BirthDate = r.Birth,
                LinkedUserSub = r.Sub,
                ConsentGranted = true,
                GamesPlayed = r.Wins + r.Draws + r.Losses,
                Wins = r.Wins,
                Draws = r.Draws,
                Losses = r.Losses,
            };
            db.Students.Add(student);

            // История рейтинга — плавный рост к текущему значению (13 точек за ~60 дней).
            int start = r.Rating - 120;
            for (int i = 0; i <= 12; i++)
                db.RatingPoints.Add(new RatingPoint
                {
                    StudentId = student.Id,
                    Date = baseDate.AddDays(i * 5),
                    Rating = start + (r.Rating - start) * i / 12,
                });
        }
    }

    private static void SeedGames(SchoolDbContext db)
    {
        var students = db.Students.Where(s => s.GroupId == GroupId).OrderBy(s => s.DisplayName).ToList();

        // Две сыгранные партии между учениками (атрибутированы чек-ином → учтены в рейтинге).
        if (students.Count >= 4)
        {
            db.Games.Add(new Game
            {
                Source = AttributionSource.CheckIn,
                Status = GameStatus.Finished,
                PlayedAt = DateTimeOffset.UtcNow.AddDays(-3),
                DeviceRef = "board-01",
                Pgn = "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 4. Ba4 Nf6 5. O-O Be7 1-0",
                WhiteStudentId = students[0].Id,
                BlackStudentId = students[1].Id,
                Result = GameResult.WhiteWins,
                EndReason = GameEndReason.Checkmate,
                WhiteRatingChange = 12,
                BlackRatingChange = -11,
            });
            db.Games.Add(new Game
            {
                Source = AttributionSource.CheckIn,
                Status = GameStatus.Finished,
                PlayedAt = DateTimeOffset.UtcNow.AddDays(-1),
                DeviceRef = "board-02",
                Pgn = "1. d4 d5 2. c4 e6 3. Nc3 Nf6 4. Bg5 Be7 5. e3 O-O 1/2-1/2",
                WhiteStudentId = students[2].Id,
                BlackStudentId = students[3].Id,
                Result = GameResult.Draw,
                EndReason = GameEndReason.DrawAgreed,
                WhiteRatingChange = 2,
                BlackRatingChange = 2,
            });
        }

        // Партии без атрибуции — попадают в очередь тренера (/attribution), рейтинг не трогают до подтверждения.
        db.Games.Add(new Game
        {
            Source = AttributionSource.None,
            Status = GameStatus.WaitingForOpponent,
            PlayedAt = DateTimeOffset.UtcNow.AddHours(-2),
            DeviceRef = "board-03",
            Pgn = "1. e4 e5 2. Nf3 Nc6 3. Bb5 a6 *",
            Result = GameResult.Ongoing,
        });
        db.Games.Add(new Game
        {
            Source = AttributionSource.None,
            Status = GameStatus.Finished,
            PlayedAt = DateTimeOffset.UtcNow.AddHours(-5),
            DeviceRef = "board-01",
            Pgn = "1. c4 c5 2. Nc3 Nc6 3. g3 g6 4. Bg2 Bg7 0-1",
            Result = GameResult.BlackWins,
            EndReason = GameEndReason.Resignation,
        });
    }
}
