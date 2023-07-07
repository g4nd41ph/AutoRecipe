using Bindito.Core;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using Timberborn.BaseComponentSystem;
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
        private ConcurrentDictionary<Manufactory, RecipeSpecification> recipeSwapsPending = new ConcurrentDictionary<Manufactory, RecipeSpecification>();

        [Inject]
        public void InjectDependencies(EventBus inEventBus)
        {
            eventBus = inEventBus;
        }

        public void Load()
        {
            eventBus.Register(this);
        }

        [OnEvent]
        public void OnDaytimeStart(DaytimeStartEvent daytimeStarted)
        {
            //Get the buildings by district, since districts may have different storage levels
            DistrictCenter[] centers = BaseComponent.FindObjectsOfType<DistrictCenter>();

            //Get a list of all the buildings
            Building[] allBuildings = BaseComponent.FindObjectsOfType<Building>();

            foreach (DistrictCenter center in centers)
            {
                //Get all buildings in this district
                List<Building> buildings = new List<Building>();
                foreach (Building current in allBuildings)
                {
                    DistrictBuilding district = current.GetComponentFast<DistrictBuilding>();
                    if (district != null && district.District != null && district.District.Equals(center))
                    {
                        buildings.Add(current);
                    }
                }

                //Build inventory stats and get list of valid manufactories to consider swapping recipes at
                Dictionary<string, StorageData> districtInventory = new Dictionary<string, StorageData>();
                List<Manufactory> manufactories = new List<Manufactory>();
                foreach (Building currentBuilding in buildings)
                {
                    //Update inventory: Do not include inventories for construction sites, district crossings,or paused buildings
                    Inventory inventory = currentBuilding.GetComponentFast<Inventory>();
                    PausableBuilding pause = currentBuilding.GetComponentFast<PausableBuilding>();
                    if (inventory != null && !inventory.ComponentName.Contains("ConstructionSite") && !inventory.ComponentName.Contains("DistrictCrossing") && pause != null && !pause.Paused)
                    {
                        //Use Allowed Goods mode for most inventories. Stockpiles and District Centers must be handled in Stock mode
                        UpdateStorageData(inventory, districtInventory, currentBuilding.GetComponentFast<Stockpile>() == null && currentBuilding.GetComponentFast<DistrictCenter>() == null);
                    }

                    //Update manufactory list: Only consider manufactories that have more than 1 recipe option, and don't swap the recipes on the mine or refinery
                    Manufactory currentManufactory = currentBuilding.GetComponentFast<Manufactory>();
                    if (currentManufactory != null && currentManufactory.ProductionRecipes.Length > 1 && !currentManufactory.name.Contains("Mine") && !currentManufactory.name.Contains("Refinery") && pause != null && !pause.Paused)
                    {
                        manufactories.Add(currentManufactory);
                    }
                }

                //Iterate through manufactories and pick the valid recipe with the lowest proportion of filled storage
                foreach (Manufactory current in manufactories)
                {
                    StorageData minStorage = null;
                    RecipeSpecification minRecipe = null;
                    foreach (RecipeSpecification currentRecipe in current.ProductionRecipes)
                    {
                        //Check for recipe validity
                        bool valid = true;
                        foreach (GoodAmount ingredient in currentRecipe.Ingredients)
                        {
                            //If there is no availability of an ingredient, do not switch to the associated recipe
                            if (!districtInventory.Keys.Contains(ingredient.GoodId) || districtInventory[ingredient.GoodId].Stock < 1)
                            {
                                valid = false;
                            }
                        }

                        //If the recipe was invalid or there is no storage for the recipe's output, do not switch to the recipe
                        if (!valid || !districtInventory.Keys.Contains(currentRecipe.Products[0].GoodId))
                        {
                            continue;
                        }

                        //Check storage levels against current lowest
                        if (districtInventory[currentRecipe.Products[0].GoodId].CompareTo(minStorage) < 0)
                        {
                            minStorage = districtInventory[currentRecipe.Products[0].GoodId];
                            minRecipe = currentRecipe;
                        }
                    }

                    //Select the minimum recipe, if a valid one was found and it's different fro mthe currently selected recipe
                    if (minRecipe != null && !minRecipe.Equals(current.CurrentRecipe))
                    {
                        //Set up the new recipe to be swapped when current production is finished
                        if (recipeSwapsPending.Keys.Contains(current))
                        {
                            recipeSwapsPending.Remove(current, out RecipeSpecification dontCare);
                        }
                        else
                        {
                            current.ProductionFinished += ProductionFinished;
                        }
                        recipeSwapsPending.TryAdd(current, current.CurrentRecipe);
                    }
                }
            }
        }

        private void ProductionFinished(object sender, EventArgs e)
        {
            //Cast sender
            Manufactory manufactory = (Manufactory)sender;

            //Clean up and deregister handler
            RecipeSpecification toSwap;
            recipeSwapsPending.Remove(manufactory, out toSwap);
            manufactory.ProductionFinished -= ProductionFinished;

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