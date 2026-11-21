using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The verbs that define an identity object on an <see cref="IIdentityHubApplicationBuilder"/>
/// from a name or an identifier and a callback.
/// </summary>
/// <remarks>
/// Each verb fills a descriptor, creates the object from it, which checks it as a whole, and
/// hands it to the builder member that takes the object. A mistake therefore throws from the
/// line that declares the object.
/// </remarks>
public static class IdentityHubApplicationBuilderExtensions
{
    extension(IIdentityHubApplicationBuilder builder)
    {
        /// <summary>
        /// Defines a user in code, under a name. The application serves it locked.
        /// </summary>
        /// <remarks>
        /// The user's identifier is derived from <paramref name="name"/> with
        /// <see cref="IdentityUserId.FromName"/>, so a user created under the name while the hub
        /// ran is the same user, and is served from code from then on.
        /// </remarks>
        /// <param name="name">The name the user signs in with.</param>
        /// <param name="configure">
        /// The callback that fills in the rest of the user. It receives a descriptor that already
        /// holds <paramref name="name"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is <see langword="default"/>, or the user breaks one of the
        /// rules of <see cref="IdentityUser.Create(IdentityUserDescriptor)"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A user with the same identifier or the same name is already defined.
        /// </exception>
        public IIdentityHubApplicationBuilder AddUser(
            IdentityUserName name,
            Action<IdentityUserDescriptor>? configure = null)
        {
            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.UserNameRequired, nameof(name));
            }

            var descriptor = new IdentityUserDescriptor { Name = name };
            configure?.Invoke(descriptor);

            return builder.AddUser(IdentityUser.Create(descriptor));
        }

        /// <summary>
        /// Defines a user in code, under an identifier. The application serves it locked.
        /// </summary>
        /// <remarks>
        /// Use it to keep a user across a change of name: the identifier is the one derived from
        /// the name the user was first defined under, and the callback gives the name it has
        /// now.
        /// </remarks>
        /// <param name="id">The user's identifier.</param>
        /// <param name="configure">
        /// The callback that fills in the user, its name included. It receives a descriptor that
        /// already holds <paramref name="id"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is <see langword="default"/>, or the user breaks one of the
        /// rules of <see cref="IdentityUser.Create(IdentityUserDescriptor)"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A user with the same identifier or the same name is already defined.
        /// </exception>
        public IIdentityHubApplicationBuilder AddUser(IdentityUserId id, Action<IdentityUserDescriptor> configure)
        {
            if (id.Value == Guid.Empty)
            {
                throw new ArgumentException(ErrorMessages.UserIdRequired, nameof(id));
            }

            ArgumentNullException.ThrowIfNull(configure);

            var descriptor = new IdentityUserDescriptor { Id = id };
            configure.Invoke(descriptor);

            return builder.AddUser(IdentityUser.Create(descriptor));
        }

        /// <summary>
        /// Defines a group in code, under a name. The application serves it locked.
        /// </summary>
        /// <remarks>
        /// The group's identifier is derived from <paramref name="name"/> with
        /// <see cref="IdentityGroupId.FromName"/>, so a group created under the name while the
        /// hub ran is the same group, and is served from code from then on.
        /// </remarks>
        /// <param name="name">The group's name.</param>
        /// <param name="configure">
        /// The callback that fills in the rest of the group. It receives a descriptor that
        /// already holds <paramref name="name"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="name"/> is <see langword="default"/>, or the group breaks one of the
        /// rules of <see cref="IdentityGroup.Create(IdentityGroupDescriptor)"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A group with the same identifier or the same name is already defined.
        /// </exception>
        public IIdentityHubApplicationBuilder AddGroup(
            IdentityGroupName name,
            Action<IdentityGroupDescriptor>? configure = null)
        {
            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.GroupNameRequired, nameof(name));
            }

            var descriptor = new IdentityGroupDescriptor { Name = name };
            configure?.Invoke(descriptor);

            return builder.AddGroup(IdentityGroup.Create(descriptor));
        }

        /// <summary>
        /// Defines a group in code, under an identifier. The application serves it locked.
        /// </summary>
        /// <remarks>
        /// Use it to keep a group across a change of name: the identifier is the one derived
        /// from the name the group was first defined under, and the callback gives the name it
        /// has now.
        /// </remarks>
        /// <param name="id">The group's identifier.</param>
        /// <param name="configure">
        /// The callback that fills in the group, its name included. It receives a descriptor
        /// that already holds <paramref name="id"/>.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="id"/> is <see langword="default"/>, or the group breaks one of the
        /// rules of <see cref="IdentityGroup.Create(IdentityGroupDescriptor)"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A group with the same identifier or the same name is already defined.
        /// </exception>
        public IIdentityHubApplicationBuilder AddGroup(IdentityGroupId id, Action<IdentityGroupDescriptor> configure)
        {
            if (id.Value == Guid.Empty)
            {
                throw new ArgumentException(ErrorMessages.GroupIdRequired, nameof(id));
            }

            ArgumentNullException.ThrowIfNull(configure);

            var descriptor = new IdentityGroupDescriptor { Id = id };
            configure.Invoke(descriptor);

            return builder.AddGroup(IdentityGroup.Create(descriptor));
        }
    }
}
