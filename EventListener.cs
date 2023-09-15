using Bindito.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.Buildings;
using Timberborn.BuildingsBlocking;
using Timberborn.GameDistricts;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.SingletonSystem;
using Timberborn.Stockpiles;
using Timberborn.TimeSystem;
using Timberborn.Workshops;

namespace AutoRecipe
{
    public class EventListener : ILoadableSingleton
    {
        private EventBus eventBus;
        private DistrictCenterRegistry centerRegistry;

        [Inject]
        public void InjectDependencies(EventBus inEventBus, DistrictCenterRegistry inRegistry)
        {
            eventBus = inEventBus;
            centerRegistry = inRegistry;
        }

        public void Load()
        {
            //Register for events from the bus
            eventBus.Register(this);
        }

        [OnEvent]
        public void OnDistrictCenterRegistryChanged(DistrictCenterRegistryChangedEvent districtCenterRegistryChanged)
        {
            //Update registrations for building added and removed events
            foreach (DistrictCenter current in centerRegistry.FinishedDistrictCenters)
            {
                //Get building registered and unregistered events
                current.DistrictBuildingRegistry.FinishedBuildingRegistered -= DistrictBuildingRegistry_FinishedBuildingRegistered;
                current.DistrictBuildingRegistry.FinishedBuildingRegistered += DistrictBuildingRegistry_FinishedBuildingRegistered;
                current.DistrictBuildingRegistry.FinishedBuildingUnregistered -= DistrictBuildingRegistry_FinishedBuildingUnregistered;
                current.DistrictBuildingRegistry.FinishedBuildingUnregistered += DistrictBuildingRegistry_FinishedBuildingUnregistered;
            }
        }

        public void DistrictBuildingRegistry_FinishedBuildingRegistered(object sender, FinishedBuildingRegisteredEventArgs e)
        {
            //Manufactories can make goods and have recipes
            Manufactory manufactory;
            if (e.Building.TryGetComponentFast<Manufactory>(out manufactory))
            {
                if (!manufactory.name.Contains("Refinery"))
                {
                    //Attach to production finished event
                    manufactory.ProductionFinished -= Manufactory_ProductionFinished;
                    manufactory.ProductionFinished += Manufactory_ProductionFinished;

                    //Attempt a swap to set a reasonable recipe to start with
                    AttemptRecipeSwap(manufactory, null);
                }
            }
        }

        public void DistrictBuildingRegistry_FinishedBuildingUnregistered(object sender, FinishedBuildingUnregisteredEventArgs e)
        {
            //If this is a manufactory, Detach from its ProductionFinished event
            Manufactory manufactory;
            if (e.Building.TryGetComponentFast<Manufactory>(out manufactory))
            {
                manufactory.ProductionFinished -= Manufactory_ProductionFinished;
            }
        }

        private void Manufactory_ProductionFinished(object sender, EventArgs e)
        {
            //Cast sender
            Manufactory manufactory = (Manufactory)sender;

            //If there is no recipe selected, attempt a swap
            if (manufactory.CurrentRecipe == null)
            {
                AttemptRecipeSwap(manufactory, null);
                return;
            }

            //Get the inventory state in a way that we have access to here
            Dictionary<string, StorageData> inventory = new Dictionary<string, StorageData>();
            UpdateStorageData(manufactory.GetComponentFast<Building>(), manufactory.Inventory, inventory);

            //Check for ingredients and space for products at the manufactory itself
            foreach (GoodAmount ingredient in manufactory.CurrentRecipe.Ingredients)
            {
                if (!inventory.ContainsKey(ingredient.GoodId) || (inventory[ingredient.GoodId].Stock < ingredient.Amount))
                {
                    AttemptRecipeSwap(manufactory, null);
                    return;
                }
            }

            //Check for space for products
            foreach (GoodAmount product in manufactory.CurrentRecipe.Products)
            {
                if (!inventory.ContainsKey(product.GoodId) || (inventory[product.GoodId].Capacity - inventory[product.GoodId].Stock < product.Amount))
                {
                    AttemptRecipeSwap(manufactory, null);
                    return;
                }
            }
        }

        [OnEvent]
        public void OnDaytimeStart(DaytimeStartEvent daytimeStarted)
        {
            //Get a list of all the districts
            DistrictCenter[] centers = centerRegistry.FinishedDistrictCenters.ToArray();

            //Go through each district and attempt swaps on all manufactories
            foreach (DistrictCenter center in centers)
            {
                //Compute center inventory
                Dictionary<string, StorageData> districtInventory = GetInventoryData(center);

                //Get the list of manufactories and attempt swaps
                foreach (Manufactory manufactory in center.DistrictBuildingRegistry.GetEnabledBuildingsInstant<Manufactory>())
                {
                    //Don't attempt a swap if this manufactory is paused
                    PausableBuilding pause = manufactory.GetComponentFast<PausableBuilding>();
                    if (pause == null || pause.Paused)
                    {
                        continue;
                    }

                    //Don't attempt a swap if production is in progress
                    if (manufactory.ProductionProgress != 0)
                    {
                        continue;
                    }

                    //Don't swap recipes at the refinery
                    if (manufactory.name.Contains("Refinery"))
                    {
                        continue;
                    }

                    //This manufactory might be stuck, attempt a swap
                    AttemptRecipeSwap(manufactory, districtInventory);
                }
            }
        }

        public void AttemptRecipeSwap(Manufactory manufactory, Dictionary<string, StorageData> districtInventory)
        {
            //Get a list of recipes that are considerable without computing inventory
            List<RecipeSpecification> validRecipes = new List<RecipeSpecification>();
            foreach (RecipeSpecification recipe in manufactory.ProductionRecipes)
            {
                //Don't consider recipes with no goods outputs
                if (recipe.Products.Count == 0)
                {
                    continue;
                }

                //All checks passed
                validRecipes.Add(recipe);
            }

            //If there are no valid choices, there's nothing to do here
            if (validRecipes.Count == 0)
            {
                return;
            }

            //If there's only one valid choice, there's no reason to waste time computing inventory levels
            if (validRecipes.Count == 1)
            {
                manufactory.SetRecipe(validRecipes[0]);
                return;
            }

            //Get inventory levels if required
            if (districtInventory == null)
            {
                DistrictBuilding districtBuilding;
                if (!manufactory.TryGetComponentFast<DistrictBuilding>(out districtBuilding))
                {
                    manufactory.SetRecipe(null);
                    return;
                }
                DistrictCenter center = districtBuilding.District;
                if (center == null)
                {
                    manufactory.SetRecipe(null);
                    return;
                }
                districtInventory = GetInventoryData(center);
            }

            //Use the inventory data to decide which recipe to swap to (if any)
            string minGoodId = "";
            StorageData minStorage = null;
            RecipeSpecification minRecipe = null;
            bool currentRecipeValid = false;
            foreach (RecipeSpecification currentRecipe in validRecipes)
            {
                //Check for recipe validity
                if (!CheckRecipeValid(districtInventory, currentRecipe))
                {
                    continue;
                }

                //Mark the current recipe as valid
                if (currentRecipe.Equals(manufactory.CurrentRecipe))
                {
                    currentRecipeValid = true;
                }

                //Check storage levels and pick the recipe with the lowest % filled storage
                foreach (GoodAmount currentProduct in currentRecipe.Products)
                {
                    if (districtInventory[currentProduct.GoodId].CompareTo(minStorage) < 0)
                    {
                        minGoodId = currentProduct.GoodId;
                        minStorage = districtInventory[currentProduct.GoodId];
                        minRecipe = currentRecipe;
                    }
                }
            }

            //If no valid recipe was found, no swap is needed
            if (minRecipe == null)
            {
                return;
            }

            //If there is no current recipe, we should definitely swap to whatever was found
            if (manufactory.CurrentRecipe == null)
            {
                manufactory.SetRecipe(minRecipe);
                return;
            }

            //If the current recipe already produces the minimum storage output and is valid, don't swap
            foreach (GoodAmount product in manufactory.CurrentRecipe.Products)
            {
                if (product.GoodId.Equals(minGoodId) && currentRecipeValid)
                {
                    return;
                }
            }

            //If the new recipe and the old recipe are the same, don't swap
            if (minRecipe.Equals(manufactory.CurrentRecipe))
            {
                return;
            }

            //Swap the recipe to teh best candidate
            manufactory.SetRecipe(minRecipe);

            //Remove the ingredients for this recipe from the district storage tracker
            foreach (GoodAmount current in minRecipe.Ingredients)
            {
                districtInventory[current.GoodId].RemoveStock(current.Amount);
            }
        }

        public Dictionary<string, StorageData> GetInventoryData(DistrictCenter center)
        {
            //Get all buildings in this district that are not paused, and get a list of all manufactories
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
            foreach (Building currentBuilding in buildings)
            {
                //Update inventory: Do not include inventories for construction sites or district crossings
                Inventory inventory = currentBuilding.GetComponentFast<Inventory>();
                if (inventory != null && !inventory.ComponentName.Contains("ConstructionSite") && !inventory.ComponentName.Contains("DistrictCrossing"))
                {
                    UpdateStorageData(currentBuilding, inventory, districtInventory);
                }
            }

            return districtInventory;
        }

        public void UpdateStorageData(Building currentBuilding, Inventory inventory, Dictionary<string, StorageData> toUpdate)
        {
            //Use allowed goods mode for Stockpiles and District Centers
            if (currentBuilding.GetComponentFast<Stockpile>() == null && currentBuilding.GetComponentFast<DistrictCenter>() == null)
            {
                //Update capacity
                foreach (StorableGoodAmount current in inventory.AllowedGoods)
                {
                    toUpdate.TryAdd(current.StorableGood.GoodId, new StorageData());
                    toUpdate[current.StorableGood.GoodId].AddCapacity(current.Amount);
                }
            }
            else
            {
                //Get capacities of current stock if allowed goods mode is not requested
                List<GoodAmount> capacities = new List<GoodAmount>();
                inventory.GetCapacity(capacities);

                //Update capacity
                foreach (GoodAmount current in capacities)
                {
                    toUpdate.TryAdd(current.GoodId, new StorageData());
                    toUpdate[current.GoodId].AddCapacity(current.Amount);
                }
            }

            //Update stock
            foreach (GoodAmount current in inventory.Stock)
            {
                toUpdate.TryAdd(current.GoodId, new StorageData());
                toUpdate[current.GoodId].AddStock(current.Amount);
            }
        }

        public bool CheckRecipeValid(Dictionary<string, StorageData> districtInventory, RecipeSpecification recipe)
        {
            //Check for supply of all ingredients
            foreach (GoodAmount ingredient in recipe.Ingredients)
            {
                if (!districtInventory.Keys.Contains(ingredient.GoodId) || districtInventory[ingredient.GoodId].Stock < ingredient.Amount)
                {
                    return false;
                }
            }

            //Check for space for all products            
            foreach (GoodAmount product in recipe.Products)
            {
                if (!districtInventory.Keys.Contains(product.GoodId) || districtInventory[product.GoodId].Capacity - districtInventory[product.GoodId].Stock < product.Amount)
                {
                    return false;
                }
            }

            //All checks OK
            return true;
        }
    }
}