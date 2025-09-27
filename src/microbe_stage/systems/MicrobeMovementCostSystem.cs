namespace Systems;

using System;
using Components;
using DefaultEcs;
using DefaultEcs.System;
using DefaultEcs.Threading;
using Godot;
using Thrive.microbe_stage;

public class MicrobeMovementCostSystem : AEntitySetSystem<float>
{
    private readonly IWorldSimulation worldSimulation;
    private readonly PhysicalWorld physicalWorld;

    public MicrobeMovementCostSystem(IWorldSimulation worldSimulation, PhysicalWorld physicalWorld, World world,
        IParallelRunner runner) : base(world, runner, Constants.SYSTEM_HIGHER_ENTITIES_PER_THREAD)
    {
        this.worldSimulation = worldSimulation;
        this.physicalWorld = physicalWorld;
    }

    protected override void Update(float delta, in Entity entity)
    {
        ref var microbeControl = ref entity.Get<MicrobeControl>();
        float requestedMoveForce = Mathf.Min(microbeControl.MovementDirection.Length(), 1.0f);
        float strainMultiplier = GetStrainAtpMultiplier(in entity);

        ref var atpBudget = ref entity.Get<AtpBudget>();
        ref OrganelleContainer organelleContainer = ref entity.Get<OrganelleContainer>();

        if (strainMultiplier > 1.0f && requestedMoveForce <= MathUtils.EPSILON)
        {
            // This is calculated similarly to the regular movement cost for consistency
            // TODO: is it fine for this to be so punishing? By taking the base movement cost here even though
            // the cell is not moving (this could take just the portion of strain multiplier that is above 1)
            HandleBaseMovementCost(ref organelleContainer, ref atpBudget, strainMultiplier, 1.0f,
                delta);
            return;
        }

        HandleBaseMovementCost(ref organelleContainer, ref atpBudget, strainMultiplier, requestedMoveForce,
            delta);
    }

    private void HandleBaseMovementCost(ref OrganelleContainer organelleContainer, ref AtpBudget atpBudget,
        float strainMultiplier, float requestedMoveForce, float delta)
    {
        var cost = Constants.BASE_MOVEMENT_ATP_COST * organelleContainer.HexCount *
            requestedMoveForce * delta * strainMultiplier;

        atpBudget.SubmitAtpRequest(cost, false);
    }

    private void HandleFlagellumMovementCost(ref OrganelleContainer organelleContainer, ref AtpBudget atpBudget,
        float strainMultiplier, float requestedMoveForce, float delta)
    {

    }

    private void HandleCilliaMovementCost(ref OrganelleContainer organelleContainer, ref AtpBudget atpBudget,
        float strainMultiplier, float requestedMoveForce, float delta)
    {

    }
    
    


    private Vector3 CalculateMovementForce(in Entity entity, ref MicrobeControl control,
        ref CellProperties cellProperties, ref WorldPosition position,
        ref OrganelleContainer organelles, CompoundBag compounds, float delta)
    {
        // TODO: switch to always reading strain affected once old save compatibility is removed
        // ref var strain = ref entity.Get<StrainAffected>();

        float strainMultiplier = 1;

        if (control.MovementDirection == Vector3.Zero)
        {
            if (entity.Has<StrainAffected>())
            {
                // TODO: move this variable up in the future
                ref var strain = ref entity.Get<StrainAffected>();
                strainMultiplier = GetStrainAtpMultiplier(ref strain);
                strain.IsUnderStrain = false;
            }

            // Remove ATP due to strain even if not moving (but only if strain is active, because otherwise this would
            // take the movement cost even while not moving meaning the editor ATP balance bar would be totally
            // inaccurate)
            if (strainMultiplier > 1)
            {
                // This is calculated similarly to the regular movement cost for consistency
                // TODO: is it fine for this to be so punishing? By taking the base movement cost here even though
                // the cell is not moving (this could take just the portion of strain multiplier that is above 1)
                var strainCost = Constants.BASE_MOVEMENT_ATP_COST * organelles.HexCount * delta * strainMultiplier;
                compounds.TakeCompound(Compound.ATP, strainCost);
            }

            // Slime jets work even when not holding down any movement keys
            var jetMovement = CalculateMovementFromSlimeJets(ref organelles);

            if (jetMovement == Vector3.Zero)
                return Vector3.Zero;

            return position.Rotation * jetMovement;
        }

        // Ensure no cells attempt to move on the y-axis
        control.MovementDirection.Y = 0;

        // Normalize if length is over 1 to not allow diagonal movement to be very fast
        var length = control.MovementDirection.Length();

        // Movement direction should not be normalized *always* to allow different speeds
        if (length > 1)
        {
            control.MovementDirection /= length;
            length = 1;
        }

        // Base movement force
        float force = MicrobeInternalCalculations.CalculateBaseMovement(cellProperties.MembraneType,
            cellProperties.MembraneRigidity, organelles.HexCount, cellProperties.IsBacteria);

        bool usesSprintingForce = false;

        if (entity.Has<StrainAffected>())
        {
            // TODO: move this variable up in the future
            ref var strain = ref entity.Get<StrainAffected>();
            strainMultiplier = GetStrainAtpMultiplier(ref strain);

            // TODO: move this if down in the future (once save compatibility is broken next time)
            if (control.Sprinting)
            {
                strain.IsUnderStrain = true;
                usesSprintingForce = true;
            }
            else
            {
                strain.IsUnderStrain = false;
            }
        }

        // Length is multiplied here so that cells that set very slow movement speed don't need to pay the entire
        // movement cost
        var cost = Constants.BASE_MOVEMENT_ATP_COST * organelles.HexCount * length * delta * strainMultiplier;

        var got = compounds.TakeCompound(Compound.ATP, cost);

        // Halve base movement speed if out of ATP
        if (got < cost)
        {
            // Not enough ATP to move at full speed
            force *= 0.5f;

            // Force out of sprint if not enough ATP
            if (usesSprintingForce)
            {
                control.Sprinting = false;

                // Under strain will reset on the next update to false
            }
        }

        // TODO: this if check can be removed (can be assumed to be present) once save compatibility is next broken
        if (entity.Has<MicrobeTemporaryEffects>())
        {
            ref var temporaryEffects = ref entity.Get<MicrobeTemporaryEffects>();

            // Apply base movement debuff if cell is currently affected by one
            if (temporaryEffects.SpeedDebuffDuration > 0)
            {
                force *= 1 - Constants.MACROLIDE_BASE_MOVEMENT_DEBUFF;
            }
        }

        // Speed from flagella (these also take ATP otherwise they won't work)
        if (organelles.FlagellumComponents != null && control.MovementDirection != Vector3.Zero)
        {
            foreach (var flagellum in organelles.FlagellumComponents)
            {
                force += flagellum.UseForMovement(control.MovementDirection, compounds, Quaternion.Identity,
                    cellProperties.IsBacteria, delta);
            }
        }

        force *= cellProperties.MembraneType.MovementFactor -
            cellProperties.MembraneRigidity * Constants.MEMBRANE_RIGIDITY_BASE_MOBILITY_MODIFIER;

        if (usesSprintingForce)
        {
            force *= Constants.SPRINTING_FORCE_MULTIPLIER;

            // TODO: put this code back once strain is guaranteed
            // strain.IsUnderStrain = true;
        }

        /*else
        {
            strain.IsUnderStrain = false;
        }*/

        bool hasColony = entity.Has<MicrobeColony>();

        if (control.MovementDirection != Vector3.Zero && hasColony)
        {
            try
            {
                CalculateColonyImpactOnMovementForce(ref entity.Get<MicrobeColony>(), control.MovementDirection,
                    cellProperties.IsBacteria, delta, ref force);
            }
            catch (Exception e)
            {
                // TODO: try to find out the real root cause of this problem rather than detecting and force disbanding
                // the colony here
                GD.PrintErr("Error calculating colony movement force: " + e);

                var entityId = entity;

                Invoke.Instance.Perform(() =>
                {
                    GD.PrintErr("Force disbanding the colony that is in invalid state, entity: ", entityId);
                    MicrobeColonyHelpers.UnbindAllOutsideGameUpdate(entityId, worldSimulation);
                });
            }
        }

        if (control.SlowedBySlime)
            force /= Constants.MUCILAGE_IMPEDE_FACTOR;

        // Movement modifier from engulf (this used to be handled in the engulfing code, now it's here)
        // TODO: should colony member engulf states be separately calculated for movement? Right now this makes it
        // very powerful to not have the primary cell type able to engulf but having other engulfing cells.
        if (control.State == MicrobeState.Engulf)
            force *= Constants.ENGULFING_MOVEMENT_MULTIPLIER;

        if (CheatManager.Speed > 1 && entity.Has<PlayerMarker>())
        {
            force *= CheatManager.Speed;
        }

        var movementVector = control.MovementDirection * force;

        // Speed from jets (these are related to a non-rotated state of the cell so this is done before rotating
        // by the transform)
        movementVector += CalculateMovementFromSlimeJets(ref organelles);

        // Handle colony jets
        if (hasColony)
        {
            // This is a duplicate fetch of this component, but this method would get pretty ugly / would need to
            // be split into many methods to allow sharing the variable
            ref var colony = ref entity.Get<MicrobeColony>();

            foreach (var colonyMember in colony.ColonyMembers)
            {
                // This doesn't really hurt as the slime jets were consumed above but for consistency with
                // basically all other places code like this is needed we skip the leader here
                if (colonyMember == entity)
                    continue;

                ref var memberOrganelles = ref colonyMember.Get<OrganelleContainer>();

                movementVector += CalculateMovementFromSlimeJets(ref memberOrganelles);
            }
        }

        // MovementDirection is proportional to the current cell rotation, so we need to rotate the movement
        // vector to work correctly
        return position.Rotation * movementVector;
    }

    private float GetStrainAtpMultiplier(in Entity entity)
    {
        if (!entity.Has<StrainAffected>())
        {
            return 1.0f;
        }

        // TODO: move this variable up in the future
        ref var strain = ref entity.Get<StrainAffected>();
        var strainFraction = strain.CalculateStrainFraction();
        return strainFraction * Constants.STRAIN_TO_ATP_USAGE_COEFFICIENT + 1.0f;
    }

    private void CalculateColonyImpactOnMovementForce(ref MicrobeColony microbeColony, Vector3 movementDirection,
        bool isBacteria, float delta, ref float force)
    {
        // If this method is updated, the CalculateSpeed() method in CellBodyPlanInternalCalculations.cs
        // also has to be changed

        CellBodyPlanInternalCalculations.ModifyCellSpeedWithColony(ref force, microbeColony.ColonyMembers.Length);

        // Colony members have their movement update before organelle update, so that the movement organelles
        // see the direction
        // The colony master should be already updated as the movement direction is either set by the
        // player input or microbe AI, neither of which will happen concurrently, so this should always get the
        // up to date value

        foreach (var colonyMember in microbeColony.ColonyMembers)
        {
            // Colony leader processes the normal movement logic so it isn't taken into account here
            if (colonyMember == microbeColony.Leader)
                continue;

            // Flagella in colony members
            ref var organelles = ref colonyMember.Get<OrganelleContainer>();

            if (organelles.FlagellumComponents != null)
            {
                var compounds = colonyMember.Get<CompoundStorage>().Compounds;
                var relativeRotation = colonyMember.Get<AttachedToEntity>().RelativeRotation;

                foreach (var flagellum in organelles.FlagellumComponents)
                {
                    force += flagellum.UseForMovement(movementDirection, compounds,
                        relativeRotation, isBacteria, delta) * Constants.CELL_COLONY_MOVEMENT_FORCE_MULTIPLIER;
                }
            }
        }
    }
}