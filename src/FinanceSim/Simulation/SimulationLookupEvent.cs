﻿using System;

namespace FinanceSim
{
  public abstract class SimulationEvent
  {
    public SimulationEvent(DateTime date)
    {
      Date = date;
    }

    public DateTime Date { get; }

    public abstract void Apply(SimulationLookupState state);

    public static SimulationEvent AdjustSnowball(DateTime date, decimal amount)
    {
      return new DelegateEvent(date, s => Simulation.AdjustSnowball(s, date, amount, "Event"));
    }

    public static SimulationEvent ChangeBillPayment(DateTime date, Bill bill, decimal newPayment)
    {
      return new UpdateBillPayment(date, bill, newPayment);
    }

    public static SimulationEvent AdjustPaycheckTotal(DateTime date, Paycheck paycheck, decimal amount)
    {
      return new DelegateEvent(date, s =>
      {
        paycheck.Total += amount;
        Simulation.AddNotice(s, date, $"Update {paycheck.Name} paycheck", amount, paycheck.Total);
      });
    }

    private class DelegateEvent : SimulationEvent
    {
      public DelegateEvent(DateTime date, Action<SimulationLookupState> action) : base(date)
      {
        Action = action;
      }

      private Action<SimulationLookupState> Action { get; }

      public override void Apply(SimulationLookupState state)
      {
        Action?.Invoke(state);
      }
    }

    private class UpdateBillPayment : SimulationEvent
    {
      public UpdateBillPayment(DateTime date, Bill bill, decimal payment) : base(date)
      {
        Bill = bill;
        Payment = payment;
      }

      private Bill Bill { get; }
      public decimal Payment { get; }

      public override void Apply(SimulationLookupState state)
      {
        var oldPayment = Bill.Payment;
        var newPayment = Payment;
        Bill.Payment = Payment;

        var diff = newPayment - oldPayment;
        if (diff > 0)
        {
          Simulation.AdjustSnowball(state, Date, -diff, $"Payment Update({Bill.Name})");
        }
      }
    }
  }
}