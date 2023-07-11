using Bindito.Core;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Timberborn.BuildingsBlocking;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.SingletonSystem;
using Timberborn.Stockpiles;
using Timberborn.TimeSystem;
using Timberborn.Workshops;
using Timberborn.Buildings;

namespace AutoRecipe
{
    public class EventListener : ILoadableSingleton
    {
        private EventBus eventBus;
        private DistrictCenterRegistry centerRegistry;
        private ConcurrentDictionary<Manufactory, RecipeSpecification> recipeSwapsPending = new ConcurrentDictionary<Manufactory, RecipeSpecification>();

        [Inject]
        public void InjectDependencies(EventBus inEventBus, DistrictCenterRegistry inRegistry)
        {
            eventBus = inEventBus;
            centerRegistry = inRegistry;
        }

        public void Load()
        {
            eventBus.Register(this);
        }

        [OnEvent]
        public void OnDaytimeStart(DaytimeStartEvent daytimeStarted)
        {
            //Get the buildings by district, since districts may have different storage levels
            DistrictCenter[] centers = centerRegistry.FinishedDistrictCenters.ToArray();

            foreach (DistrictCenter center in centers)
            {
                //Get all buildings in this district that are not paused
                List<Building> buildings = new List<Building>();
                foreach (Building current in center.DistrictBuildingRegistry.GetEnabledBuildingsInstant<Building>())
                {
                    PausableBuilding pause = current.GetComponentFast<PausableBuilding>();
                    if (pause != null && !pause.Paused)
                    {
                        buildings.Add(current);
                    }
                }

                //Build inventory stats and get list of valid manufactories to consider swapping recipes at
                Dictionary<string, StorageData> districtInventory = new Dictionary<string, StorageData>();
                List<Manufactory> manufactories = new List<Manufactory>();
                foreach (Building currentBuilding in buildings)
                {
                    //Update inventory: Do not include inventories for construction sites or district crossings
                    Inventory inventory = currentBuilding.GetComponentFast<Inventory>();
                    if (inventory != null && !inventory.ComponentName.Contains("ConstructionSite") && !inventory.ComponentName.Contains("DistrictCrossing"))
                    {
                        //Use Allowed Goods mode for most inventories. Stockpiles and District Centers must be handled in Stock mode
                        UpdateStorageData(inventory, districtInventory, currentBuilding.GetComponentFast<Stockpile>() == null && currentBuilding.GetComponentFast<DistrictCenter>() == null);
                    }

                    //Update manufactory list: Only consider manufactories that have more than 1 recipe option, and don't swap the recipes on the refinery
                    Manufactory currentManufactory = currentBuilding.GetComponentFast<Manufactory>();
                    if (currentManufactory != null && currentManufactory.ProductionRecipes.Length > 1 && !currentManufactory.name.Contains("Refinery"))
                    {
                        manufactories.Add(currentManufactory);
                    }
                }

                //Iterate through manufactories and pick the valid recipe with the lowest proportion of filled storage
                foreach (Manufactory current in manufactories)
                {
                    StorageData minStorage = null;
                    RecipeSpecification minRecipe = null;
                    int validCount = 0;
                    foreach (RecipeSpecification currentRecipe in current.ProductionRecipes)
                    {
                        //Check for recipe validity
                        if (!CheckRecipeValid(districtInventory, currentRecipe))
                        {
                            continue;
                        }
                        validCount++;

                        //Check storage levels and pick the recipe with the lowest % filled storage
                        foreach (GoodAmount currentProduct in currentRecipe.Products)
                        {
                            if (districtInventory[currentProduct.GoodId].CompareTo(minStorage) < 0)
                            {
                                minStorage = districtInventory[currentProduct.GoodId];
                                minRecipe = currentRecipe;
                            }
                        }
                    }

                    //Select the minimum recipe, if a valid one was found from among more than one option and it's different from the currently selected recipe
                    if (minRecipe != null && validCount > 1 && !minRecipe.Equals(current.CurrentRecipe))
                    {
                        //We want to replace any currently queued swap
                        if (recipeSwapsPending.Keys.Contains(current))
                        {
                            current.ProductionFinished -= ProductionFinished;
                            recipeSwapsPending.Remove(current, out RecipeSpecification dontCare);
                        }

                        //If no production is in progress, we can swap right away
                        if (current.ProductionProgress == 0)
                        {
                            current.SetRecipe(minRecipe);
                        }

                        //If production is ongoing, we should queue a swap when it finishes
                        else
                        {
                            current.ProductionFinished += ProductionFinished;
                            recipeSwapsPending.TryAdd(current, minRecipe);
                        }
                    }
                }
            }
        }

        private bool CheckRecipeValid(Dictionary<string, StorageData> districtInventory, RecipeSpecification recipe)
        {
            //Check for ingredients
            foreach (GoodAmount ingredient in recipe.Ingredients)
            {
                //If there is no availability of an ingredient, do not switch to the associated recipe
                if (!districtInventory.Keys.Contains(ingredient.GoodId) || districtInventory[ingredient.GoodId].Stock < ingredient.Amount)
                {
                    return false;
                }
            }

            //Don't consider recipes with no good outputs
            if (recipe.Products.Count == 0)
            {
                return false;
            }

            //Check for space for all products            
            foreach (GoodAmount current in recipe.Products)
            {
                if (!districtInventory.Keys.Contains(current.GoodId) || districtInventory[current.GoodId].Capacity - districtInventory[current.GoodId].Stock < current.Amount)
                {
                    return false;
                }
            }

            //All checks OK
            return true;
        }

        private void ProductionFinished(object sender, EventArgs e)
        {
            //Cast sender
            Manufactory manufactory = (Manufactory)sender;

            //Clean up and deregister handler
            manufactory.ProductionFinished -= ProductionFinished;
            RecipeSpecification toSwap;
            recipeSwapsPending.Remove(manufactory, out toSwap);

            //Execute the swap
            manufactory.SetRecipe(toSwap);
        }

        public void UpdateStorageData(Inventory inventory, Dictionary<string, StorageData> toUpdate, bool useAllowedGoods)
        {
            if (useAllowedGoods)
            {
                //Use allowed goods mode if requested
                foreach (StorableGoodAmount current in inventory.AllowedGoods)
                {
                    toUpdate.TryAdd(current.StorableGood.GoodId, new StorageData());
                    toUpdate[current.StorableGood.GoodId].UpdateCapacity(current.Amount);
                }
            }
            else
            {
                //Get capacities of current stock if allowed goods mode is not requested
                List<GoodAmount> capacities = new List<GoodAmount>();
                inventory.GetCapacity(capacities);

                //Update Capacities
                foreach (GoodAmount current in capacities)
                {
                    toUpdate.TryAdd(current.GoodId, new StorageData());
                    toUpdate[current.GoodId].UpdateCapacity(current.Amount);
                }
            }

            //Update stock
            foreach (GoodAmount current in inventory.Stock)
            {
                toUpdate.TryAdd(current.GoodId, new StorageData());
                toUpdate[current.GoodId].UpdateStock(current.Amount);
            }
        }
    }
}