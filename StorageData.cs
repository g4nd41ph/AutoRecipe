using System;
using Timberborn.Goods;

namespace AutoRecipe
{
    public class StorageData : IComparable
    {
        //Members
        int stock;
        int capacity;

        //Constructor
        public StorageData() {
            stock = 0;
            capacity = 0;
        }

        //Methods
        public void UpdateStock(int addStock)
        {
            stock += addStock;
        }

        public void UpdateCapacity(int addCapacity)
        {
            capacity += addCapacity;
        }

        public int CompareTo(object other)
        {
            try
            {
                //Cast
                StorageData otherStorage = (StorageData) other;

                //Check for zero capacity in either StorageData
                if (this.Capacity == 0 || otherStorage.Capacity == 0)
                {
                    return otherStorage.Capacity.CompareTo(this.Capacity);
                }

                //Return the value with minimum storage usage
                return ((double)this.Stock / (double)this.Capacity).CompareTo((double)otherStorage.Stock / (double)otherStorage.Capacity);

            }
            catch (Exception ex)
            {
                //If the cast fails, then other is null or is not a StorageData. Either way, this instance should be less.
                return -1;
            }
        }

        //Properties
        public int Stock
        {
            get
            {
                return stock;
            }
        }

        public int Capacity
        {
            get
            {
                return capacity;
            }
        }
    }
}
