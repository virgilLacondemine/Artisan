﻿using Artisan.CraftingLogic.CraftData;
using Artisan.RawInformation.Character;
using Dalamud.Utility;
using ECommons.DalamudServices;
using System;
using System.ComponentModel;

namespace Artisan.CraftingLogic;

public static class Simulator
{
    public enum CraftStatus
    {
        [Description("Craft in progress")]
        InProgress,
        [Description("Craft failed due to durability")]
        FailedDurability,
        [Description("Craft failed due to minimum quality not being met")]
        FailedMinQuality,
        [Description($"Craft has completed 1st quality breakpoint")]
        SucceededQ1,
        [Description($"Craft has completed 2nd quality breakpoint")]
        SucceededQ2,
        [Description($"Craft has completed 3rd quality breakpoint")]
        SucceededQ3,
        [Description($"Craft has completed with max quality")]
        SucceededMaxQuality,
        [Description($"Craft has completed without max quality")]
        SucceededSomeQuality,
        [Description($"Craft has completed, no quality required")]
        SucceededNoQualityReq,

        Count
    }

    public static string ToOutputString(this CraftStatus status)
    {
        return status.GetAttribute<DescriptionAttribute>().Description;
    }

    public enum ExecuteResult
    {
        CantUse,
        Failed,
        Succeeded
    }

    public static StepState CreateInitial(CraftState craft, int startingQuality)
        => new() { Index = 1, Durability = craft.CraftDurability, Quality = startingQuality, RemainingCP = craft.StatCP, CarefulObservationLeft = craft.Specialist ? 3 : 0, HeartAndSoulAvailable = craft.Specialist, Condition = Condition.Normal };

    public static CraftStatus Status(CraftState craft, StepState step)
    {
        if (step.Progress < craft.CraftProgress)
        {
            if (step.Durability > 0)
                return CraftStatus.InProgress;
            else
                return CraftStatus.FailedDurability;
        }

        if (craft.CraftCollectible || craft.CraftExpert)
        {
            if (step.Quality >= craft.CraftQualityMin3)
                return CraftStatus.SucceededQ3;

            if (step.Quality >= craft.CraftQualityMin2)
                return CraftStatus.SucceededQ2;

            if (step.Quality >= craft.CraftQualityMin1)
                return CraftStatus.SucceededQ1;

            if (step.Quality < craft.CraftRequiredQuality || step.Quality < craft.CraftQualityMin1)
                return CraftStatus.FailedMinQuality;

        }

        if (craft.CraftHQ && !craft.CraftCollectible)
        {
            if (step.Quality >= craft.CraftQualityMax)
                return CraftStatus.SucceededMaxQuality;
            else
                return CraftStatus.SucceededSomeQuality;

        }
        else
        {
            return CraftStatus.SucceededNoQualityReq;
        }
    }

    public static (ExecuteResult, StepState) Execute(CraftState craft, StepState step, Skills action, float actionSuccessRoll, float nextStateRoll)
    {
        if (Status(craft, step) != CraftStatus.InProgress)
            return (ExecuteResult.CantUse, step); // can't execute action on craft that is not in progress

        var success = actionSuccessRoll < GetSuccessRate(step, action);

        if (!CanUseAction(craft, step, action))
            return (ExecuteResult.CantUse, step); // can't use action because of level, insufficient cp or special conditions

        var next = new StepState();
        next.Index = SkipUpdates(action) ? step.Index : step.Index + 1;
        next.Progress = step.Progress + (success ? CalculateProgress(craft, step, action) : 0);
        next.Quality = step.Quality + (success ? CalculateQuality(craft, step, action) : 0);
        next.IQStacks = step.IQStacks;
        if (success)
        {
            if (next.Quality != step.Quality)
                ++next.IQStacks;
            if (action is Skills.PreciseTouch or Skills.PreparatoryTouch or Skills.Reflect)
                ++next.IQStacks;
            if (next.IQStacks > 10)
                next.IQStacks = 10;
            if (action == Skills.ByregotsBlessing)
                next.IQStacks = 0;
        }

        next.WasteNotLeft = action switch
        {
            Skills.WasteNot => GetNewBuffDuration(step, 4),
            Skills.WasteNot2 => GetNewBuffDuration(step, 8),
            _ => GetOldBuffDuration(step.WasteNotLeft, action)
        };
        next.ManipulationLeft = action == Skills.Manipulation ? GetNewBuffDuration(step, 8) : GetOldBuffDuration(step.ManipulationLeft, action);
        next.GreatStridesLeft = action == Skills.GreatStrides ? GetNewBuffDuration(step, 3) : GetOldBuffDuration(step.GreatStridesLeft, action, next.Quality != step.Quality);
        next.InnovationLeft = action == Skills.Innovation ? GetNewBuffDuration(step, 4) : GetOldBuffDuration(step.InnovationLeft, action);
        next.VenerationLeft = action == Skills.Veneration ? GetNewBuffDuration(step, 4) : GetOldBuffDuration(step.VenerationLeft, action);
        next.MuscleMemoryLeft = action == Skills.MuscleMemory ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.MuscleMemoryLeft, action, next.Progress != step.Progress);
        next.FinalAppraisalLeft = action == Skills.FinalAppraisal ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.FinalAppraisalLeft, action, next.Progress >= craft.CraftProgress);
        next.CarefulObservationLeft = step.CarefulObservationLeft - (action == Skills.CarefulObservation ? 1 : 0);
        next.HeartAndSoulActive = action == Skills.HeartAndSoul || step.HeartAndSoulActive && (step.Condition is Condition.Good or Condition.Excellent || !ConsumeHeartAndSoul(action));
        next.HeartAndSoulAvailable = step.HeartAndSoulAvailable && action != Skills.HeartAndSoul;
        next.PrevActionFailed = !success;
        next.PrevComboAction = action; // note: even stuff like final appraisal and h&s break combos

        if (step.FinalAppraisalLeft > 0 && next.Progress >= craft.CraftProgress)
            next.Progress = craft.CraftProgress - 1;

        next.RemainingCP = step.RemainingCP - GetCPCost(step, action);
        if (action == Skills.TricksOfTrade) // can't fail
            next.RemainingCP = Math.Min(craft.StatCP, next.RemainingCP + 20);

        // assume these can't fail
        next.Durability = step.Durability - GetDurabilityCost(step, action);
        if (next.Durability > 0)
        {
            int repair = 0;
            if (action == Skills.MastersMend)
                repair += 30;
            if (step.ManipulationLeft > 0 && action != Skills.Manipulation && !SkipUpdates(action))
                repair += 5;
            next.Durability = Math.Min(craft.CraftDurability, next.Durability + repair);
        }

        next.Condition = action is Skills.FinalAppraisal or Skills.HeartAndSoul ? step.Condition : GetNextCondition(craft, step, nextStateRoll);

        return (success ? ExecuteResult.Succeeded : ExecuteResult.Failed, next);
    }

    public static int BaseProgress(CraftState craft)
    {
        float res = craft.StatCraftsmanship * 10.0f / craft.CraftProgressDivider + 2;
        if (craft.StatLevel <= craft.CraftLevel) // TODO: verify this condition, teamcraft uses 'rlvl' here
            res = res * craft.CraftProgressModifier / 100;
        return (int)res;
    }

    public static int BaseQuality(CraftState craft)
    {
        float res = craft.StatControl * 10.0f / craft.CraftQualityDivider + 35;
        if (craft.StatLevel <= craft.CraftLevel) // TODO: verify this condition, teamcraft uses 'rlvl' here
            res = res * craft.CraftQualityModifier / 100;
        return (int)res;
    }

    public static int MinLevel(Skills action) => action switch
    {
        Skills.BasicSynthesis => 1,
        Skills.CarefulSynthesis => 62,
        Skills.RapidSynthesis => 9,
        Skills.FocusedSynthesis => 67,
        Skills.Groundwork => 72,
        Skills.IntensiveSynthesis => 78,
        Skills.PrudentSynthesis => 88,
        Skills.MuscleMemory => 54,
        Skills.BasicTouch => 5,
        Skills.StandardTouch => 18,
        Skills.AdvancedTouch => 84,
        Skills.HastyTouch => 9,
        Skills.FocusedTouch => 68,
        Skills.PreparatoryTouch => 71,
        Skills.PreciseTouch => 53,
        Skills.PrudentTouch => 66,
        Skills.TrainedFinesse => 90,
        Skills.Reflect => 69,
        Skills.ByregotsBlessing => 50,
        Skills.TrainedEye => 80,
        Skills.DelicateSynthesis => 76,
        Skills.Veneration => 15,
        Skills.Innovation => 26,
        Skills.GreatStrides => 21,
        Skills.TricksOfTrade => 13,
        Skills.MastersMend => 7,
        Skills.Manipulation => 65,
        Skills.WasteNot => 15,
        Skills.WasteNot2 => 47,
        Skills.Observe => 13,
        Skills.CarefulObservation => 55,
        Skills.FinalAppraisal => 42,
        Skills.HeartAndSoul => 86,
        _ => 0
    };

    public static bool CanUseAction(CraftState craft, StepState step, Skills action) => action switch
    {
        Skills.IntensiveSynthesis or Skills.PreciseTouch or Skills.TricksOfTrade => step.Condition is Condition.Good or Condition.Excellent || step.HeartAndSoulActive,
        Skills.PrudentSynthesis or Skills.PrudentTouch => step.WasteNotLeft == 0,
        Skills.MuscleMemory or Skills.Reflect => step.Index == 1,
        Skills.TrainedFinesse => step.IQStacks == 10,
        Skills.ByregotsBlessing => step.IQStacks > 0,
        Skills.TrainedEye => !craft.CraftExpert && craft.StatLevel >= craft.CraftLevel + 10 && step.Index == 1,
        Skills.Manipulation => craft.UnlockedManipulation,
        Skills.CarefulObservation => step.CarefulObservationLeft > 0,
        Skills.HeartAndSoul => step.HeartAndSoulAvailable,
        _ => true
    } && craft.StatLevel >= MinLevel(action) && step.RemainingCP >= GetCPCost(step, action);

    public static bool SkipUpdates(Skills action) => action is Skills.CarefulObservation or Skills.FinalAppraisal or Skills.HeartAndSoul;
    public static bool ConsumeHeartAndSoul(Skills action) => action is Skills.IntensiveSynthesis or Skills.PreciseTouch or Skills.TricksOfTrade;

    public static double GetSuccessRate(StepState step, Skills action)
    {
        var rate = action switch
        {
            Skills.FocusedSynthesis or Skills.FocusedTouch => step.PrevComboAction == Skills.Observe ? 1.0 : 0.5,
            Skills.RapidSynthesis => 0.5,
            Skills.HastyTouch => 0.6,
            _ => 1.0
        };
        if (step.Condition == Condition.Centered)
            rate += 0.25;
        return rate;
    }

    public static int GetBaseCPCost(Skills action, Skills prevAction) => action switch
    {
        Skills.CarefulSynthesis => 7,
        Skills.FocusedSynthesis => 5,
        Skills.Groundwork => 18,
        Skills.IntensiveSynthesis => 6,
        Skills.PrudentSynthesis => 18,
        Skills.MuscleMemory => 6,
        Skills.BasicTouch => 18,
        Skills.StandardTouch => prevAction == Skills.BasicTouch ? 18 : 32,
        Skills.AdvancedTouch => prevAction == Skills.StandardTouch ? 18 : 46,
        Skills.FocusedTouch => 18,
        Skills.PreparatoryTouch => 40,
        Skills.PreciseTouch => 18,
        Skills.PrudentTouch => 25,
        Skills.TrainedFinesse => 32,
        Skills.Reflect => 6,
        Skills.ByregotsBlessing => 24,
        Skills.TrainedEye => 250,
        Skills.DelicateSynthesis => 32,
        Skills.Veneration => 18,
        Skills.Innovation => 18,
        Skills.GreatStrides => 32,
        Skills.MastersMend => 88,
        Skills.Manipulation => 96,
        Skills.WasteNot => 56,
        Skills.WasteNot2 => 98,
        Skills.Observe => 7,
        Skills.FinalAppraisal => 1,
        _ => 0
    };

    public static int GetCPCost(StepState step, Skills action)
    {
        var cost = GetBaseCPCost(action, step.PrevComboAction);
        if (step.Condition == Condition.Pliant)
            cost -= cost / 2; // round up
        return cost;
    }

    public static int GetDurabilityCost(StepState step, Skills action)
    {
        var cost = action switch
        {
            Skills.BasicSynthesis or Skills.CarefulSynthesis or Skills.RapidSynthesis or Skills.FocusedSynthesis or Skills.IntensiveSynthesis or Skills.MuscleMemory => 10,
            Skills.BasicTouch or Skills.StandardTouch or Skills.AdvancedTouch or Skills.HastyTouch or Skills.FocusedTouch or Skills.PreciseTouch or Skills.Reflect => 10,
            Skills.ByregotsBlessing or Skills.DelicateSynthesis => 10,
            Skills.Groundwork or Skills.PreparatoryTouch => 20,
            Skills.PrudentSynthesis or Skills.PrudentTouch => 5,
            _ => 0
        };
        if (step.WasteNotLeft > 0)
            cost -= cost / 2; // round up
        if (step.Condition == Condition.Sturdy)
            cost -= cost / 2; // round up
        return cost;
    }

    public static int GetNewBuffDuration(StepState step, int baseDuration) => baseDuration + (step.Condition == Condition.Primed ? 2 : 0);
    public static int GetOldBuffDuration(int prevDuration, Skills action, bool consume = false) => consume || prevDuration == 0 ? 0 : SkipUpdates(action) ? prevDuration : prevDuration - 1;

    public static int CalculateProgress(CraftState craft, StepState step, Skills action)
    {
        int potency = action switch
        {
            Skills.BasicSynthesis => craft.StatLevel >= 31 ? 120 : 100,
            Skills.CarefulSynthesis => craft.StatLevel >= 82 ? 180 : 150,
            Skills.RapidSynthesis => craft.StatLevel >= 63 ? 500 : 250,
            Skills.FocusedSynthesis => 200,
            Skills.Groundwork => step.Durability >= GetDurabilityCost(step, action) ? craft.StatLevel >= 86 ? 360 : 300 : craft.StatLevel >= 86 ? 180 : 150,
            Skills.IntensiveSynthesis => 400,
            Skills.PrudentSynthesis => 180,
            Skills.MuscleMemory => 300,
            Skills.DelicateSynthesis => 100,
            _ => 0
        };
        if (potency == 0)
            return 0;

        float buffMod = 1 + (step.MuscleMemoryLeft > 0 ? 1 : 0) + (step.VenerationLeft > 0 ? 0.5f : 0);
        float effPotency = potency * buffMod;

        float condMod = step.Condition == Condition.Malleable ? 1.5f : 1;
        return (int)(BaseProgress(craft) * condMod * effPotency / 100);
    }

    public static int CalculateQuality(CraftState craft, StepState step, Skills action)
    {
        if (action == Skills.TrainedEye)
            return craft.CraftQualityMax;

        int potency = action switch
        {
            Skills.BasicTouch => 100,
            Skills.StandardTouch => 125,
            Skills.AdvancedTouch => 150,
            Skills.HastyTouch => 100,
            Skills.FocusedTouch => 150,
            Skills.PreparatoryTouch => 200,
            Skills.PreciseTouch => 150,
            Skills.PrudentTouch => 100,
            Skills.TrainedFinesse => 100,
            Skills.Reflect => 100,
            Skills.ByregotsBlessing => 100 + 20 * step.IQStacks,
            Skills.DelicateSynthesis => 100,
            _ => 0
        };
        if (potency == 0)
            return 0;

        float buffMod = (1 + 0.1f * step.IQStacks) * (1 + (step.GreatStridesLeft > 0 ? 1 : 0) + (step.InnovationLeft > 0 ? 0.5f : 0));
        float effPotency = potency * buffMod;

        float condMod = step.Condition switch
        {
            Condition.Good => craft.Splendorous ? 1.75f : 1.5f,
            Condition.Excellent => 4,
            Condition.Poor => 0.5f,
            _ => 1
        };
        return (int)(BaseQuality(craft) * condMod * effPotency / 100);
    }

    public static bool WillFinishCraft(CraftState craft, StepState step, Skills action) => step.FinalAppraisalLeft == 0 && step.Progress + CalculateProgress(craft, step, action) >= craft.CraftProgress;

    public static Skills NextTouchCombo(StepState step) => step.PrevComboAction switch
    {
        Skills.BasicTouch => Skills.StandardTouch,
        Skills.StandardTouch => Skills.AdvancedTouch,
        _ => Skills.BasicTouch
    };

    public static Condition GetNextCondition(CraftState craft, StepState step, float roll) => step.Condition switch
    {
        Condition.Normal => GetTransitionByRoll(craft, step, roll),
        Condition.Good => craft.CraftExpert ? GetTransitionByRoll(craft, step, roll) : Condition.Normal,
        Condition.Excellent => Condition.Poor,
        Condition.Poor => Condition.Normal,
        Condition.GoodOmen => Condition.Good,
        _ => GetTransitionByRoll(craft, step, roll)
    };

    public static Condition GetTransitionByRoll(CraftState craft, StepState step, float roll)
    {
        for (int i = 1; i < craft.CraftConditionProbabilities.Length; ++i)
        {
            roll -= craft.CraftConditionProbabilities[i];
            if (roll < 0)
                return (Condition)i;
        }
        return Condition.Normal;
    }
}
