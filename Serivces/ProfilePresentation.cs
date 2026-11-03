using Zullo.Api.Dtos;
using Zullo.Api.Models;
namespace Zullo.Api.Services;

public static class ProfilePresentation
{
    public static SwipeProfileDto ToDto(Profile p, Profile viewer, bool superLike = false) => new SwipeProfileDto
            {
                UserId = p.UserId,
                SuperLikedYou = superLike,

                // skyddar mot null och konstiga värden
                DisplayName = (p.DisplayName ?? "").Trim(),
                Age = p.Age,
                Bio = (p.Bio ?? "").Trim(),
                PromptKey = p.PromptKey, PromptAnswer = p.PromptAnswer,

                Intention = (p.Intention ?? "").Trim(),
                Religion = (p.Religion ?? "").Trim(),
                Workout = (p.Workout ?? "").Trim(),
                Smoking = (p.Smoking ?? "").Trim(),
                Pets = (p.Pets ?? "").Trim(),

                HeightCm = p.HeightCm,

                RelationshipHistory = (p.RelationshipHistory ?? "").Trim(),
                ZodiacSign = (p.ZodiacSign ?? "").Trim(),

                Alcohol = (p.Alcohol ?? "").Trim(),
                Cannabis = (p.Cannabis ?? "").Trim(),

                ChildrenCount = (p.ChildrenCount ?? "").Trim(),
                WantChildren = (p.WantChildren ?? "").Trim(),

                WorkStatus = (p.WorkStatus ?? "").Trim(),
                StudyPlace = (p.StudyPlace ?? "").Trim(),
                StudySubject = (p.StudySubject ?? "").Trim(),
                WorkPlace = (p.WorkPlace ?? "").Trim(),
                JobTitle = (p.JobTitle ?? "").Trim(),

                LivePlace = (p.LivePlace ?? "").Trim(),
                OriginPlace = (p.OriginPlace ?? "").Trim(),

                // säkerställ att listor aldrig är null
                Interests = p.Interests ?? new List<string>(),
                PhotoUrls = p.PhotoUrls ?? new List<string>(),

                // första bild om den finns
                PhotoUrl = p.PhotoUrls?.FirstOrDefault() ?? "",

                CountryCode = (p.CountryCode ?? "").Trim(),

                DistanceKm = Math.Round(
                    GeoService.DistanceKm(viewer.Lat, viewer.Lng, p.Lat, p.Lng),
                    1
                )
            };
}
