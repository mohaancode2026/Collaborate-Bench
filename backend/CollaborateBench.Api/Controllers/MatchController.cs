using System.Data;
using CollaborateBench.Api.Data;
using CollaborateBench.Api.Infrastructure;
using CollaborateBench.Api.Models;
using CollaborateBench.Api.Security;
using CollaborateBench.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CollaborateBench.Api.Controllers;

/// <summary>Match agent: explainable fit scores between consultants and open requirements (see Services/Matching).</summary>
[ApiController, Route("api/match")]
public sealed class MatchController(DataService data) : ControllerBase
{
    /// <summary>Every open requirement scored for one consultant, best first, with the stage if already submitted.</summary>
    [HttpGet("consultant/{id:int}"), RequirePerm("match.view")]
    public async Task<List<MatchResult>> ForConsultant(int id)
    {
        var c = await data.ConsultantAsync(id);
        var subs = (await data.SubmissionsAsync(null)).Where(s => s.ConsultantId == id).ToDictionary(s => s.RequirementId, s => s.Stage);
        return (await data.RequirementsAsync(true)).Select(r => Matching.Score(c, r, subs.GetValueOrDefault(r.Id))).OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>Every available consultant in your view scored for one requirement, best first.</summary>
    [HttpGet("requirement/{id:int}"), RequirePerm("match.view")]
    public async Task<List<MatchResult>> ForRequirement(int id, [FromQuery] string? scope)
    {
        var r = await data.RequirementAsync(id);
        var subs = (await data.SubmissionsAsync(null)).Where(s => s.RequirementId == id).ToDictionary(s => s.ConsultantId, s => s.Stage);
        return (await data.ConsultantsAsync(scope)).Where(c => c.Status is "Bench" or "Marketing")
            .Select(c => Matching.Score(c, r, subs.GetValueOrDefault(c.Id))).OrderByDescending(m => m.Score).ToList();
    }
}
