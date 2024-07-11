# Alice Mail Service (AMS)

Many applications have operations that generate email notifications (e.g., an order confirmation email
when an order is placed). When email notification is implemented as part of the operation, the operation
will feel very slow as sending an email is quite time-consuming. The proper way to handle this is to
put email mesasges in a queue and have a separate service send them. This way, the operation can complete
quickly and the emails can be sent in the background. Alice Mail Service (AMS) is such a service. It gets
email messages from a RabbitMQ queue and sends them out via an SMTP server.