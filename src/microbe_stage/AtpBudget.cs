namespace Thrive.microbe_stage;

using System;
using System.Linq;
using Godot;
using Newtonsoft.Json;

public struct AtpBudget()
{
    // MandatoryAtpRequired is used to store the amount of atp needed by a cell for essential
    // functions that result in damage if they aren't run.
    [JsonIgnore]
    public float MandatoryAtpRequired = 0.0f;

    // DiscretionaryAtpRequired is used to store the amount of atp needed by a cell for
    // functions that can be safely scaled down if there is not enough energy.
    [JsonIgnore]
    public float DiscretionaryAtpRequired = 0.0f;

    // AtpAllocatedToBudget is used to store the atp that has been taken from compound storage
    // or produced this frame that has been set aside to run the cell functions for this frame
    [JsonIgnore]
    public float AtpAllocatedToBudget = 0.0f;
}

public static class AtpRequestsHelper
{
    public static void SubmitAtpRequest(this ref AtpBudget atpBudget, float atp, bool isMandatory)
    {
        if (atp < 0)
        {
            // TODO: should we have some sort of warning here?
            return;
        }

        if (isMandatory)
        {
            atpBudget.MandatoryAtpRequired += atp;
        }
        else
        {
            atpBudget.DiscretionaryAtpRequired += atp;
        }
    }

    public static float GetOutstandingAtpRequired(this in AtpBudget atpBudget)
    {
        return atpBudget.MandatoryAtpRequired + atpBudget.DiscretionaryAtpRequired - atpBudget.AtpAllocatedToBudget;
    }

    public static bool IsMandatoryAtpRequirementCovered(this in AtpBudget atpBudget)
    {
        return atpBudget.AtpAllocatedToBudget >= atpBudget.MandatoryAtpRequired;
    }

    public static float ClaimDiscretionaryAtp(this ref AtpBudget atpBudget, float atpRequested)
    {
        float available = Mathf.Max(atpBudget.AtpAllocatedToBudget - atpBudget.MandatoryAtpRequired, 0.0f);
        if (atpRequested <= MathUtils.EPSILON || available <= MathUtils.EPSILON)
        {
            return 0.0f;
        }

        available = Math.Min(available, atpRequested);
        atpBudget.AtpAllocatedToBudget -= available;
        atpBudget.MandatoryAtpRequired -= available;
        return available;
    }

    public static void TryFillAtpBudgetFromStorage(this ref AtpBudget atpBudget, in CompoundBag compoundBag)
    {
        atpBudget.AtpAllocatedToBudget += compoundBag.TakeCompound(Compound.ATP, GetOutstandingAtpRequired(atpBudget));
    }

    public static float FillAtpRequests(this ref AtpBudget atpBudget, float availableAtp)
    {
        if (availableAtp <= 0)
        {
            return 0f;
        }

        float requiredAtp = GetOutstandingAtpRequired(atpBudget);
        atpBudget.AtpAllocatedToBudget += Mathf.Min(requiredAtp, availableAtp);

        return Mathf.Max(availableAtp - requiredAtp, 0.0f);
    }

    public static void Reset(this ref AtpBudget atpBudget)
    {
        atpBudget.MandatoryAtpRequired = 0.0f;
        atpBudget.DiscretionaryAtpRequired = 0.0f;
        atpBudget.AtpAllocatedToBudget = 0.0f;
    }
}
