﻿// ==============================================================================================================
// Microsoft patterns & practices
// CQRS Journey project
// ==============================================================================================================
// ©2012 Microsoft. All rights reserved. Certain content used with permission from contributors
// http://cqrsjourney.github.com/contributors/members
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance 
// with the License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software distributed under the License is 
// distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
// See the License for the specific language governing permissions and limitations under the License.
// ==============================================================================================================

namespace Conference
{
    using System;
    using System.Data.Entity;
    using System.Linq;
    using System.Linq.Expressions;
    using Infrastructure.Messaging.Handling;
    using Registration.Events;

    // DEV NOTE:
    // This denormalized version of an order is being created in the Conference Management BC, via events
    // coming from the Registration BC (generated by event sourced aggregates).
    // ALL the information is there to generate this denormalized version, but nevertheless, the events
    // seem too granular to keep up with, considering this is a different and isolated bounded context.

    // We feel that reusing the same events that the Registration BC uses internally (for both event sourcing
    // in the write-model, and generating projections for the read model) for integrating between different
    // BCs starts to make the 2 BCs very coupled (regardless of that coupling being through messaging).

    // An alternative is that the Registration BC can create a denormalization like this one on "the other side",
    // and at one point in time publish an event specifically made for integration, with a full dump
    // of the order information.
    // A slightly different alternative, is that we generate the projection on the other side, and publish
    // an event notifying the listeners that a new order is ready, so they (the Conference Mgmt BC)
    // can (asynchronously) make a direct service call and get the information for the order
    // (still getting the fully denormalized order in a single service call).
    public class OrderEventHandler :
        IEventHandler<OrderPlaced>,
        IEventHandler<OrderRegistrantAssigned>,
        IEventHandler<OrderTotalsCalculated>,
        IEventHandler<OrderPaymentConfirmed>,
        IEventHandler<SeatAssignmentsCreated>,
        IEventHandler<SeatAssigned>,
        IEventHandler<SeatAssignmentUpdated>,
        IEventHandler<SeatUnassigned>
    {
        private Func<ConferenceContext> contextFactory;

        public OrderEventHandler(Func<ConferenceContext> contextFactory)
        {
            this.contextFactory = contextFactory;
        }

        public void Handle(OrderPlaced @event)
        {
            using (var context = this.contextFactory.Invoke())
            {
                context.Orders.Add(new Order(@event.ConferenceId, @event.SourceId, @event.AccessCode));
                context.SaveChanges();
            }
        }

        public void Handle(OrderRegistrantAssigned @event)
        {
            ProcessOrder(order => order.Id == @event.SourceId, order =>
            {
                order.RegistrantEmail = @event.Email;
                order.RegistrantName = @event.LastName + ", " + @event.FirstName;
            });
        }

        public void Handle(OrderTotalsCalculated @event)
        {
            ProcessOrder(order => order.Id == @event.SourceId, order => order.TotalAmount = @event.Total);
        }

        public void Handle(OrderPaymentConfirmed @event)
        {
            ProcessOrder(order => order.Id == @event.SourceId, order => order.Status = Order.OrderStatus.Paid);
        }

        public void Handle(SeatAssignmentsCreated @event)
        {
            ProcessOrder(order => order.Id == @event.OrderId, order => order.AssignmentsId = @event.SourceId);
        }

        public void Handle(SeatAssigned @event)
        {
            ProcessOrder(order => order.AssignmentsId == @event.SourceId, order =>
            {
                var seat = order.Seats.FirstOrDefault(x => x.Position == @event.Position);
                if (seat != null)
                {
                    seat.Attendee.FirstName = @event.Attendee.FirstName;
                    seat.Attendee.LastName = @event.Attendee.LastName;
                    seat.Attendee.Email = @event.Attendee.Email;
                }
                else
                {
                    order.Seats.Add(new OrderSeat(@event.SourceId, @event.Position, @event.SeatType)
                    {
                        Attendee = new Attendee
                        {
                            FirstName = @event.Attendee.FirstName,
                            LastName = @event.Attendee.LastName,
                            Email = @event.Attendee.Email,
                        }
                    });
                }
            });
        }

        public void Handle(SeatAssignmentUpdated @event)
        {
            ProcessOrder(order => order.AssignmentsId == @event.SourceId, order =>
            {
                var seat = order.Seats.FirstOrDefault(x => x.Position == @event.Position);
                if (seat != null)
                {
                    seat.Attendee.FirstName = @event.Attendee.FirstName;
                    seat.Attendee.LastName = @event.Attendee.LastName;
                }
            });
        }

        public void Handle(SeatUnassigned @event)
        {
            ProcessOrder(order => order.AssignmentsId == @event.SourceId, order =>
            {
                var seat = order.Seats.FirstOrDefault(x => x.Position == @event.Position);
                if (seat != null)
                {
                    order.Seats.Remove(seat);
                }
            });
        }

        private void ProcessOrder(Expression<Func<Order, bool>> lookup, Action<Order> orderAction)
        {
            using (var context = this.contextFactory.Invoke())
            {
                var order = context.Orders.Include(x => x.Seats).FirstOrDefault(lookup);
                if (order != null)
                {
                    orderAction.Invoke(order);
                    context.SaveChanges();
                }
            }
        }
    }
}
