using ChessSchool.ApiService.Data;
using ChessSchool.ApiService.Domain;
using ChessSchool.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ChessSchool.ApiService.Services;

/// <summary>
/// Авторизация доступа к ЛК школы по владению: пользователь (IdP-`sub`) видит/меняет только СВОЮ школу.
/// Плюс провижининг «моя школа» (get-or-create). Резолв школы ученика — через `Student.GroupId → Group.SchoolId`.
/// </summary>
/// <summary>Опции провижининга школы. В Development новосозданная школа наполняется примерными учениками
/// (чтобы ЛK dev-пользователя не был пустым); в проде новая школа создаётся пустой.</summary>
public sealed record SchoolProvisioningOptions(bool SeedSampleStudents);

public sealed class SchoolAccessService(SchoolDbContext db, SchoolProvisioningOptions options)
{
    /// <summary>Владеет ли пользователь школой.</summary>
    public Task<bool> OwnsSchoolAsync(string sub, Guid schoolId, CancellationToken ct) =>
        db.Schools.AsNoTracking().AnyAsync(s => s.Id == schoolId && s.OwnerSub == sub, ct);

    /// <summary>Владеет ли пользователь школой, к которой относится ученик (через его группу).</summary>
    public async Task<bool> OwnsStudentAsync(string sub, Guid studentId, CancellationToken ct) =>
        await SchoolIdOfStudentAsync(studentId, ct) is { } schoolId && await OwnsSchoolAsync(sub, schoolId, ct);

    /// <summary>SchoolId ученика (Student.GroupId → Group.SchoolId) или null, если ученика нет.</summary>
    public Task<Guid?> SchoolIdOfStudentAsync(Guid studentId, CancellationToken ct) =>
        (from st in db.Students.AsNoTracking()
         join g in db.Groups on st.GroupId equals g.Id
         where st.Id == studentId
         select (Guid?)g.SchoolId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Get-or-create: возвращает школу владельца (или создаёт новую + дефолтную группу). Идемпотентно:
    /// повторный вызов вернёт ту же школу. Возможная гонка первого визита (две вкладки) → редкий дубль
    /// школы; последующие вызовы стабильно берут первую (FirstOrDefault).
    /// </summary>
    public async Task<MySchoolDto> EnsureSchoolForAsync(string sub, CancellationToken ct)
    {
        var school = await db.Schools.FirstOrDefaultAsync(s => s.OwnerSub == sub, ct);
        if (school is null)
        {
            school = new School { OwnerSub = sub, Name = "Моя школа" };
            db.Schools.Add(school);
            db.Groups.Add(new Group { SchoolId = school.Id, Name = "Основная группа" });
            await db.SaveChangesAsync(ct);
        }
        var groupId = await db.Groups.AsNoTracking()
            .Where(g => g.SchoolId == school.Id)
            .OrderBy(g => g.Name)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(ct);

        // В Development наполняем ПУСТУЮ школу примерными учениками (и новосозданную, и уже существующую
        // пустую — иначе dev, у кого школа завелась пустой при прошлом входе, так и видел бы пустой ЛК).
        // В проде школа остаётся пустой — реальные ученики добавляются вручную. Гард по «нет учеников»:
        // повторный вызов на непустой школе ничего не делает (но опустошённую dev-школу переселит — приемлемо).
        if (options.SeedSampleStudents && groupId is { } gid
            && !await db.Students.AnyAsync(s => s.GroupId == gid, ct))
        {
            SeedData.AddSampleStudents(db, gid);
            await db.SaveChangesAsync(ct);
        }
        return new MySchoolDto(school.Id, groupId ?? Guid.Empty);
    }
}
