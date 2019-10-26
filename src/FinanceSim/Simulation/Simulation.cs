﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FinanceSim
{
  public static class Simulation
  {
    public static async Task<Dictionary<IAccount, List<Transaction>>> Run(SimulationSetup setup, Profile profile)
    {
      var cloned = await Task.Run(() => profile.Clone());
      return await Task.Run(() => RunInternal(setup, cloned));
    }

    private static Dictionary<IAccount, List<Transaction>> RunInternal(SimulationSetup setup, Profile profile)
    {
      var state = InitializeState(setup, profile);

      // while there are debts remaining
      for (DateTime date = state.Start; KeepGoing(date); date = date.AddDays(1))
      {
        // if needed, add the interest to the debt balances
        ApplyIterest(date, state);

        // apply any events that may be pending
        ApplyEvents(date, state);

        // process the remaining items
        for (int i = 0; i < state.RemainingItems.Count; ++i)
        {
          var item = state.RemainingItems[i];
          var next = state.NextDueDate[item.Id];

          if (next == date)
          {
            var (remove, newSnowball) = ProcessDueItem(date, item, state);
            if (remove)
            {
              state.RemainingItems.RemoveAt(i);
              --i;

              if (newSnowball && SelectNextSnowballTarget(state))
              {
                state.IsDone = true;
                break;
              }
            }

            state.NextDueDate[item.Id] = DueInfoHelper.Advance(next, item.Due);
          }
        }

        if (state.IsDone)
        {
          break;
        }
      }

      return state.Transactions;

      bool KeepGoing(DateTime currentDate)
      {
        if (state.UseSnowball)
        {
          return state.RemainingItems.OfType<Debt>().Any();
        }
        else
        {
          return currentDate <= setup.End;
        }
      }
    }

    private static void ApplyEvents(DateTime date, SimulationState state)
    {
      date = date.Date;
      foreach (var item in state.Events)
      {
        if (item.Date.Date == date)
        {
          item.Apply(state);
        }
      }
    }

    private static bool SelectNextSnowballTarget(SimulationState state)
    {
      if (state.SnowballTargets.Any())
      {
        var nextSnowballTarget = state.SnowballTargets.Dequeue();
        while (!state.RemainingItems.Contains(nextSnowballTarget))
        {
          if (state.SnowballTargets.Any())
          {
            nextSnowballTarget = state.SnowballTargets.Dequeue();
          }
          else
          {
            return true;
          }
        }
        state.CurrentSnowballTarget = nextSnowballTarget;
      }
      else
      {
        return true;
      }

      return false;
    }

    private static (bool remove, bool newSnowball) ProcessDueItem(DateTime date, IHasDueInfo item, SimulationState state)
    {
      bool remove = false;
      bool newSnowball = false;

      if (item is Paycheck paycheck)
      {
        var amount = paycheck.Total;
        foreach (var d in paycheck.Deposits)
        {
          var value = d.Amount;
          Deposit(state, date, d.AccountId, d.Name, value);
          amount -= value;
        }

        Deposit(state, date, paycheck.AccountId, paycheck.Name, amount);
      }
      else if (item is Debt debt)
      {
        var payment = debt.Payment;
        if (state.UseSnowball && state.CurrentSnowballTarget == debt)
        {
          payment += state.SnowballAmount;
          newSnowball = true;
        }

        // take the payment from the account
        Withdraw(state, date, debt.AccountId, debt.Name, payment);

        // reduce the balance since we made a payment
        var newBalance = state.DebtBalances[debt.Id] - payment;
        if (newBalance <= 0m)
        {
          newBalance = 0;
          PaidOff(state, date, debt);

          if (state.UseSnowball)
          {
            AdjustSnowball(state, date, debt.Payment);
          }

          remove = true;
        }

        // report the payment
        AddTransaction(state, debt, date, "Payment", payment, newBalance);

        // a payment decreases the balance
        state.DebtBalances[debt.Id] = newBalance;
      }
      else if (item is Bill bill)
      {
        Withdraw(state, date, bill.AccountId, bill.Name, bill.Payment);
      }

      return (remove, newSnowball);
    }

    public static void AdjustSnowball(SimulationState state, DateTime date, decimal diff)
    {
      state.SnowballAmount += diff;
      AddTransaction(state, state.NoticeAccount, date, "Snowball Update", diff, state.SnowballAmount);
    }

    public static void AddNotice(SimulationState state, DateTime date, string text, decimal amount = 0, decimal balance = 0)
    {
      AddTransaction(state, state.NoticeAccount, date, text, amount, balance);
    }

    public static void PaidOff(SimulationState state, DateTime date, Debt debt)
    {
      AddTransaction(state, state.NoticeAccount, date, $"Paid Off: {debt.Name}", 0, 0);
    }

    public static void NegativeBalance(SimulationState state, DateTime date, IAccount account)
    {
      AddTransaction(state, state.NoticeAccount, date, $"ALERT: {account.Name}", 0, state.AccountBalances[account.Id]);
    }

    private static void Deposit(SimulationState state, DateTime date, string id, string name, decimal amount)
    {
      state.AccountBalances[id] += amount;
      var newBalance = state.AccountBalances[id];
      AddTransaction(state, state.Accounts[id], date, name, amount, newBalance);
    }

    private static void Withdraw(SimulationState state, DateTime date, string id, string name, decimal amount)
    {
      var account = state.Accounts[id];
      state.AccountBalances[id] -= amount;

      var newBalance = state.AccountBalances[id];
      AddTransaction(state, account, date, name, -amount, newBalance);
      if (newBalance < 0)
      {
        NegativeBalance(state, date, account);
      }
    }

    private static SimulationState InitializeState(SimulationSetup setup, Profile profile)
    {
      var state = new SimulationState();
      state.Transactions = new Dictionary<IAccount, List<Transaction>>();
      state.Start = setup.Start;
      state.RemainingItems = profile.OfType<IHasDueInfo>().ToList();
      state.NextDueDate = InitializeDueDates(state);
      state.Accounts = profile.OfType<IAccount>().ToDictionary(a => a.Id);
      state.AccountBalances = profile.OfType<IAccount>().ToDictionary(k => k.Id, v => v.Balance);
      state.DebtBalances = state.RemainingItems.OfType<Debt>().ToDictionary(k => k.Id, v => v.Balance);
      state.UseSnowball = setup.UseSnowball;
      state.SnowballAmount = profile.Snowball?.InitialAmount ?? 0m;
      state.SnowballTargets = new Queue<Debt>(state.RemainingItems
       .OfType<Debt>()
       .OrderBy(d => GetSnowballOrder(d, profile.Snowball)));
      state.CurrentSnowballTarget = state.SnowballTargets.Dequeue();
      state.Events = GenerateEvents(profile, state).ToList();
      return state;
    }

    private static IEnumerable<SimulationEvent> GenerateEvents(Profile profile, SimulationState state)
    {
      var events = profile.Events;
      if (events != null)
      {
        var bills = profile.OfType<Bill>().ToDictionary(b => b.Id);
        var paychecks = profile.OfType<Paycheck>().ToDictionary(p => p.Id);

        foreach (var item in events.AdjustSnowballAmountEvents)
        {
          yield return SimulationEvent.AdjustSnowball(item.Date, item.Amount);
        }

        foreach (var item in events.ChangeBillPaymentEvents)
        {
          if (bills.TryGetValue(item.BillId, out var bill))
          {
            yield return SimulationEvent.ChangeBillPayment(item.Date, bill, item.NewPayment);
          }
        }

        foreach (var item in events.AdjustPaycheckTotalEvents)
        {
          if (paychecks.TryGetValue(item.PaycheckId, out var paycheck))
          {
            yield return SimulationEvent.AdjustPaycheckTotal(item.Date, paycheck, item.Amount);
          }
        }
      }
    }

    private static Dictionary<string, DateTime> InitializeDueDates(SimulationState state)
    {
      var nextDueDate = new Dictionary<string, DateTime>();
      for (int i = state.RemainingItems.Count - 1; i > -1; --i)
      {
        var item = state.RemainingItems[i];
        var next = item.Due.Seed;
        while (next < state.Start || next < item.Due.Start)
        {
          next = DueInfoHelper.Advance(next, item.Due);
          if (next >= item.Due.End)
          {
            // the item is no longer due
            state.RemainingItems.RemoveAt(i);
            break;
          }
        }
        nextDueDate[item.Id] = next;
      }
      return nextDueDate;
    }

    private static int GetSnowballOrder(Debt debt, SnowBallSetup snowball)
    {
      if (snowball == null)
      {
        return (int)Math.Round(debt.Balance);
      }

      int index = Array.IndexOf(snowball.DebtOrder, debt.Id);
      return (index < 0) ? int.MaxValue : index;
    }

    private static void ApplyIterest(DateTime date, SimulationState state)
    {
      var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
      if (date.Day == daysInMonth)
      {
        foreach (var debt in state.RemainingItems.OfType<Debt>().Where(d => d.Interest > 0))
        {
          var monthly = debt.Interest / 12;
          var interest = state.DebtBalances[debt.Id] * monthly;
          if (interest > 0)
          {
            // interest increases the balance
            decimal newBalance = state.DebtBalances[debt.Id] + interest;
            state.DebtBalances[debt.Id] = newBalance;
            AddTransaction(state, debt, date, $"{debt.Interest:P2} Interest", interest, newBalance);
          }
        }
      }
    }

    private static void AddTransaction(SimulationState state, IAccount account, DateTime date, string text, decimal amount, decimal balance)
    {
      if (!state.Transactions.TryGetValue(account, out var transactions))
      {
        transactions = new List<Transaction>();
        state.Transactions[account] = transactions;
      }

      transactions.Add(new Transaction
      {
        Amount = amount,
        Balance = balance,
        Date = date,
        Name = text,
      });
    }
  }
}
